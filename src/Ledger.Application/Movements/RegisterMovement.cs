using Ledger.Application.Abstractions;
using Ledger.Application.Observability;
using Ledger.Domain;

namespace Ledger.Application.Movements;

/// <summary>Pedido de registro de uma movimentação.</summary>
/// <param name="AccountId">Conta a movimentar.</param>
/// <param name="Type">Crédito ou débito.</param>
/// <param name="Amount">Quantia, sempre positiva.</param>
/// <param name="Currency">Código ISO 4217 da moeda.</param>
/// <param name="OccurredAt">Quando o fato aconteceu, no mundo.</param>
/// <param name="IdempotencyKey">Chave que torna a repetição segura. Obrigatória.</param>
/// <param name="CorrelationId">Correlação da requisição.</param>
/// <param name="ExternalReference">Referência no sistema de origem, opcional.</param>
/// <remarks>
/// Note o que <b>não</b> está aqui: o instante de registro. Ele é decisão do
/// servidor — aceitar o relógio de quem chama permitiria antedatar ou pós-datar
/// um lançamento.
/// </remarks>
public sealed record RegisterMovementCommand(
    Guid AccountId,
    MovementType Type,
    decimal Amount,
    string Currency,
    DateTimeOffset OccurredAt,
    string IdempotencyKey,
    string CorrelationId,
    string? ExternalReference = null);

/// <summary>Desfecho de um pedido de registro.</summary>
public enum RegisterMovementStatus
{
    /// <summary>Valor não atribuído. Nunca deve ocorrer.</summary>
    Unspecified = 0,

    /// <summary>Movimentação registrada agora. A API responde <c>201</c>.</summary>
    Registered,

    /// <summary>
    /// A mesma requisição já havia sido registrada. A API responde <c>200</c>
    /// com a resposta original.
    /// </summary>
    /// <remarks>
    /// É replay, não reprocessamento: o identificador e o instante devolvidos são
    /// os da primeira vez. Devolver um identificador novo para o mesmo fato
    /// criaria a divergência que a idempotência existe para evitar, já que o
    /// chamador pode estar usando esse identificador para conciliar.
    /// </remarks>
    Replayed,

    /// <summary>
    /// A chave já foi usada com conteúdo diferente. A API responde <c>409</c>.
    /// </summary>
    ConflictingPayload,

    /// <summary>A conta não existe. A API responde <c>404</c>.</summary>
    AccountNotFound,

    /// <summary>
    /// O pedido viola uma regra de negócio. A API responde <c>400</c>.
    /// </summary>
    Rejected,
}

/// <summary>Resultado de um pedido de registro.</summary>
/// <param name="Status">Desfecho.</param>
/// <param name="MovementId">Identificador da movimentação, quando houver uma.</param>
/// <param name="PostedAt">Instante do registro, quando houver um.</param>
/// <param name="ErrorCode">Invariante violada, quando o pedido foi recusado.</param>
/// <param name="Detail">Explicação legível, quando houver.</param>
/// <param name="Movement">
/// A movimentação criada, presente apenas em
/// <see cref="RegisterMovementStatus.Registered"/>.
/// <para>
/// Existe para que a resposta seja montada a partir do que foi <b>gravado</b>, e
/// não a partir do pedido. Os dois não são a mesma coisa: o pedido pode trazer
/// 150,75 e instante com precisão de 100 ns, e o razão guarda 150,7500 e
/// microssegundos. Montar a resposta do pedido faria o registro afirmar valores
/// que o razão não tem — e faria a resposta do replay, lida do banco,
/// discordar da resposta original.
/// </para>
/// </param>
public sealed record RegisterMovementResult(
    RegisterMovementStatus Status,
    Guid? MovementId = null,
    DateTimeOffset? PostedAt = null,
    DomainErrorCode? ErrorCode = null,
    string? Detail = null,
    Movement? Movement = null);

/// <summary>
/// Política temporal e de retenção do registro de movimentação.
/// </summary>
/// <remarks>
/// Estes limites são <b>política</b>, não invariante de domínio, e é por isso que
/// moram aqui e não em <see cref="Movement"/>: a verificação correta depende de
/// uma tolerância, e tolerância precisa ser configurável por quem opera o
/// sistema.
/// </remarks>
public sealed record RegisterMovementOptions
{
    /// <summary>
    /// Quanto o instante do fato pode estar à frente do relógio do servidor.
    /// </summary>
    /// <remarks>
    /// Não é zero de propósito. Relógios de servidores diferentes divergem por
    /// alguns segundos mesmo com NTP, e recusar por isso rejeitaria movimentação
    /// legítima enviada "agora" por um sistema cujo relógio está um pouco à
    /// frente. O que o limite impede é data absurda — ano 2099 — não desvio
    /// normal.
    /// </remarks>
    public TimeSpan MaxFutureSkew { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>Quão antigo o instante do fato pode ser.</summary>
    /// <remarks>
    /// Movimentação retroativa existe e é legítima; ver PR-04. O limite existe
    /// para barrar erro de digitação ou de conversão de data — ano 1900 — que de
    /// outra forma entraria no razão e invalidaria snapshots de anos inteiros.
    /// </remarks>
    public TimeSpan MaxBackdating { get; init; } = TimeSpan.FromDays(30);

    /// <summary>Por quanto tempo a chave de idempotência é lembrada.</summary>
    /// <remarks>
    /// A janela cobre folgadamente qualquer retry plausível. Sem expiração, a
    /// tabela cresceria para sempre guardando chaves que ninguém vai repetir.
    /// </remarks>
    public TimeSpan IdempotencyRetention { get; init; } = TimeSpan.FromDays(7);
}

/// <summary>
/// Registra uma movimentação no razão, de forma segura para repetição.
/// </summary>
/// <remarks>
/// <para>
/// O fluxo é: validar a entrada, carregar a conta, deixar o domínio construir o
/// fato, e tentar gravar. Se a chave de idempotência já existir, comparar a
/// impressão do conteúdo para decidir entre replay e conflito.
/// </para>
/// <para>
/// <b>Não há consulta prévia do tipo "esta chave já existe?".</b> Ela deixaria
/// duas requisições simultâneas passarem, porque ambas veriam "não existe". A
/// detecção acontece na violação do constraint, que é avaliada pelo banco no
/// commit e não depende de ordem de execução.
/// </para>
/// </remarks>
public sealed class RegisterMovementHandler(
    ILedgerRepository repository,
    IClock clock,
    RegisterMovementOptions? options = null,
    LedgerMetrics? metrics = null)
{
    private readonly RegisterMovementOptions _options = options ?? new RegisterMovementOptions();

    /// <summary>Executa o pedido.</summary>
    public async Task<RegisterMovementResult> HandleAsync(
        RegisterMovementCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var agora = clock.UtcNow;

        AccountId contaId;
        Money valor;
        IdempotencyKey chave;

        try
        {
            contaId = AccountId.From(command.AccountId);
            valor = Money.Of(command.Amount, Currency.Of(command.Currency));
            chave = IdempotencyKey.Of(command.IdempotencyKey);
        }
        catch (DomainException erro)
        {
            return Recusar(erro);
        }

        // Trunca o instante de negócio à precisão que o razão guarda. Sem isso,
        // a resposta afirmaria nanossegundos que o banco descartou, e a leitura
        // posterior do mesmo lançamento devolveria outro valor.
        var ocorridaEm = LedgerTimePolicy.Truncate(command.OccurredAt);

        if (ValidarJanelaTemporal(ocorridaEm, agora) is { } problemaTemporal)
        {
            return problemaTemporal;
        }

        var conta = await repository.FindAccountAsync(contaId, cancellationToken)
            .ConfigureAwait(false);

        if (conta is null)
        {
            return new RegisterMovementResult(
                RegisterMovementStatus.AccountNotFound,
                Detail: $"Conta {command.AccountId} não encontrada.");
        }

        Movement movimentacao;

        try
        {
            movimentacao = conta.Register(
                command.Type,
                valor,
                ocorridaEm,
                agora,
                chave,
                command.CorrelationId,
                command.ExternalReference);
        }
        catch (DomainException erro)
        {
            return Recusar(erro);
        }

        var impressao = RequestFingerprint.Of(movimentacao);

        var desfecho = await repository.TryRegisterAsync(
            movimentacao,
            impressao,
            agora.Add(_options.IdempotencyRetention),
            cancellationToken).ConfigureAwait(false);

        if (desfecho == LedgerWriteOutcome.Persisted)
        {
            metrics?.MovementPosted(command.Type);

            return new RegisterMovementResult(
                RegisterMovementStatus.Registered,
                movimentacao.Id.Value,
                movimentacao.PostedAt,
                Movement: movimentacao);
        }

        return await ResolverChaveJaUsadaAsync(chave, impressao, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<RegisterMovementResult> ResolverChaveJaUsadaAsync(
        IdempotencyKey chave,
        string impressaoAtual,
        CancellationToken cancellationToken)
    {
        var original = await repository.FindRegistrationAsync(chave, cancellationToken)
            .ConfigureAwait(false);

        if (original is null)
        {
            // A chave existia no momento do INSERT e não existe agora. O caminho
            // plausível é a limpeza das chaves expiradas ter passado entre as
            // duas operações.
            //
            // A resposta é conflito, e não "registre de novo": a movimentação
            // original pode estar no razão, e repetir duplicaria dinheiro.
            // Recusar é a única opção segura — e o chamador precisa investigar,
            // não insistir.
            metrics?.IdempotencyConflict();

            return new RegisterMovementResult(
                RegisterMovementStatus.ConflictingPayload,
                Detail: "A chave de idempotência já foi usada, mas o registro "
                      + "original não foi encontrado. Pode ter expirado. "
                      + "Verifique o extrato antes de repetir com esta chave.");
        }

        if (string.Equals(original.RequestFingerprint, impressaoAtual, StringComparison.Ordinal))
        {
            metrics?.IdempotentReplay();

            return new RegisterMovementResult(
                RegisterMovementStatus.Replayed,
                original.MovementId,
                original.RegisteredAt);
        }

        metrics?.IdempotencyConflict();

        return new RegisterMovementResult(
            RegisterMovementStatus.ConflictingPayload,
            Detail: "A chave de idempotência já foi usada com um conteúdo "
                  + "diferente. Use uma chave nova para uma movimentação nova.");
    }

    private RegisterMovementResult? ValidarJanelaTemporal(DateTimeOffset ocorridaEm, DateTimeOffset agora)
    {
        if (ocorridaEm == default)
        {
            return new RegisterMovementResult(
                RegisterMovementStatus.Rejected,
                ErrorCode: DomainErrorCode.MissingTimestamp,
                Detail: "Instante do fato é obrigatório.");
        }

        if (ocorridaEm > agora.Add(_options.MaxFutureSkew))
        {
            return new RegisterMovementResult(
                RegisterMovementStatus.Rejected,
                ErrorCode: DomainErrorCode.MissingTimestamp,
                Detail: $"Instante do fato está no futuro além da tolerância de "
                      + $"{_options.MaxFutureSkew.TotalSeconds:F0} segundos.");
        }

        return ocorridaEm < agora.Subtract(_options.MaxBackdating)
            ? new RegisterMovementResult(
                RegisterMovementStatus.Rejected,
                ErrorCode: DomainErrorCode.MissingTimestamp,
                Detail: $"Instante do fato é anterior ao limite de retroatividade "
                      + $"de {_options.MaxBackdating.TotalDays:F0} dias.")
            : null;
    }

    private RegisterMovementResult Recusar(DomainException erro)
    {
        metrics?.MovementRejected(erro.Code);
        return new(RegisterMovementStatus.Rejected, ErrorCode: erro.Code, Detail: erro.Message);
    }
}
