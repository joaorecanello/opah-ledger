namespace Ledger.Domain;

/// <summary>
/// Movimentação financeira: um fato imutável no razão contábil.
/// </summary>
/// <remarks>
/// <para>
/// <b>Imutável por construção.</b> Não há propriedade com <c>set</c>, não há
/// método que altere estado. O ledger é append-only: o saldo nunca muda, ele só
/// se acumula. Correção de erro é movimentação compensatória que referencia a
/// original, nunca alteração do registro — o que preserva a trilha de auditoria
/// que o domínio exige.
/// </para>
/// <para>
/// <b>Dois instantes, não um.</b> <see cref="OccurredAt"/> é o tempo de negócio
/// (quando o fato aconteceu no mundo) e <see cref="PostedAt"/> é o tempo de
/// sistema (quando foi registrado). São perguntas diferentes e ambas legítimas:
/// "qual era meu saldo na sexta" usa a primeira; "o que o sistema sabia na
/// sexta" usa a segunda, e é a pergunta de auditoria e de investigação de
/// incidente. Guardar só um fecha metade das perguntas para sempre.
/// </para>
/// <para>
/// <b>O que este tipo deliberadamente não valida.</b> Não há verificação de
/// <see cref="OccurredAt"/> estar no futuro, nem de saldo suficiente. A primeira
/// é política e depende de uma tolerância a desvio de relógio entre servidores,
/// logo pertence à camada que pode configurá-la. A segunda é autorização, que
/// não é deste sistema — ver PR-03 em docs/premissas.md.
/// </para>
/// </remarks>
public sealed class Movement
{
    /// <summary>Tamanho máximo da referência externa.</summary>
    public const int MaxExternalReferenceLength = 128;

    /// <summary>Tamanho máximo do identificador de correlação.</summary>
    public const int MaxCorrelationIdLength = 128;

    private Movement(
        MovementId id,
        AccountId accountId,
        MovementType type,
        Money amount,
        DateTimeOffset occurredAt,
        DateTimeOffset postedAt,
        IdempotencyKey idempotencyKey,
        string correlationId,
        string? externalReference)
    {
        Id = id;
        AccountId = accountId;
        Type = type;
        Amount = amount;
        OccurredAt = occurredAt;
        PostedAt = postedAt;
        IdempotencyKey = idempotencyKey;
        CorrelationId = correlationId;
        ExternalReference = externalReference;
    }

    /// <summary>Identificador da movimentação, ordenável por tempo.</summary>
    public MovementId Id { get; }

    /// <summary>Conta movimentada.</summary>
    public AccountId AccountId { get; }

    /// <summary>Natureza da movimentação.</summary>
    public MovementType Type { get; }

    /// <summary>
    /// Quantia movimentada, <b>sempre positiva</b>.
    /// </summary>
    /// <remarks>
    /// O sinal é semântica de <see cref="Type"/>, não do número. Isso impede a
    /// classe de bug em que um crédito com valor negativo se comporta como
    /// débito sem que nada no código pareça errado.
    /// </remarks>
    public Money Amount { get; }

    /// <summary>Quando o fato aconteceu (tempo de negócio).</summary>
    public DateTimeOffset OccurredAt { get; }

    /// <summary>
    /// Quando o sistema registrou o fato (tempo de sistema).
    /// </summary>
    /// <remarks>
    /// Definido pelo servidor, nunca pelo cliente: aceitar o relógio de quem
    /// chama permitiria antedatar ou pós-datar um registro.
    /// </remarks>
    public DateTimeOffset PostedAt { get; }

    /// <summary>Chave que torna o registro seguro para repetição.</summary>
    public IdempotencyKey IdempotencyKey { get; }

    /// <summary>
    /// Identificador de correlação da requisição que originou o registro.
    /// </summary>
    /// <remarks>
    /// Persistido junto do fato, e não apenas emitido em log. Quando alguém
    /// contestar um lançamento meses depois, isto é o que permite recuperar a
    /// requisição original e o sistema que a originou — mesmo depois de o log
    /// ter saído da retenção.
    /// </remarks>
    public string CorrelationId { get; }

    /// <summary>Referência no sistema de origem, quando houver.</summary>
    public string? ExternalReference { get; }

    /// <summary>
    /// Quantia com o sinal aplicado: positiva em crédito, negativa em débito.
    /// </summary>
    /// <remarks>
    /// É esta projeção que o cálculo de saldo soma. A conversão fica em um só
    /// lugar, para que nenhum chamador precise lembrar da convenção de sinal.
    /// </remarks>
    public Money SignedAmount => Type == MovementType.Credit ? Amount : Amount.Negate();

    /// <summary>
    /// Reconstrói uma movimentação já persistida, sem reaplicar as regras de
    /// criação.
    /// </summary>
    /// <remarks>
    /// Para uso da camada de persistência. Um fato que já está no razão é
    /// histórico, e histórico não se revalida: se a regra mudar amanhã, os
    /// lançamentos de ontem continuam sendo o que foram.
    /// </remarks>
    public static Movement Rehydrate(
        MovementId id,
        AccountId accountId,
        MovementType type,
        Money amount,
        DateTimeOffset occurredAt,
        DateTimeOffset postedAt,
        IdempotencyKey idempotencyKey,
        string correlationId,
        string? externalReference)
    {
        ArgumentNullException.ThrowIfNull(correlationId);

        return new Movement(
            id,
            accountId,
            type,
            amount,
            occurredAt,
            postedAt,
            idempotencyKey,
            correlationId,
            externalReference);
    }

    internal static Movement Create(
        AccountId accountId,
        Currency accountCurrency,
        MovementType type,
        Money amount,
        DateTimeOffset occurredAt,
        DateTimeOffset postedAt,
        IdempotencyKey idempotencyKey,
        string correlationId,
        string? externalReference)
    {
        if (type is not (MovementType.Credit or MovementType.Debit))
        {
            throw DomainException.For(
                DomainErrorCode.InvalidMovementType,
                $"Tipo de movimentação não reconhecido: {(int)type}.");
        }

        if (amount.Currency.IsUnset)
        {
            throw DomainException.For(
                DomainErrorCode.MissingCurrency,
                "Movimentação exige moeda no valor.");
        }

        if (amount.Currency != accountCurrency)
        {
            throw DomainException.For(
                DomainErrorCode.CurrencyMismatch,
                $"Movimentação em {amount.Currency.Code} não pertence a uma conta "
                + $"em {accountCurrency.Code}.");
        }

        if (!amount.IsPositive)
        {
            throw DomainException.For(
                DomainErrorCode.AmountNotPositive,
                $"Valor da movimentação deve ser maior que zero. Recebido: {amount}. "
                + "O sinal é dado pelo tipo da movimentação, não pelo número.");
        }

        if (occurredAt == default)
        {
            throw DomainException.For(
                DomainErrorCode.MissingTimestamp,
                "Instante do fato (occurredAt) é obrigatório.");
        }

        if (postedAt == default)
        {
            throw DomainException.For(
                DomainErrorCode.MissingTimestamp,
                "Instante de registro (postedAt) é obrigatório.");
        }

        if (idempotencyKey.IsUnset)
        {
            throw DomainException.For(
                DomainErrorCode.InvalidIdempotencyKey,
                "Chave de idempotência é obrigatória no registro de movimentação.");
        }

        var correlacao = ExigirTexto(
            correlationId,
            MaxCorrelationIdLength,
            "Identificador de correlação");

        var referencia = TextoOpcional(
            externalReference,
            MaxExternalReferenceLength,
            "Referência externa");

        return new Movement(
            MovementId.NewAt(postedAt),
            accountId,
            type,
            amount,
            occurredAt,
            postedAt,
            idempotencyKey,
            correlacao,
            referencia);
    }

    private static string ExigirTexto(string? valor, int tamanhoMaximo, string nomeDoCampo)
    {
        if (string.IsNullOrWhiteSpace(valor))
        {
            throw DomainException.For(
                DomainErrorCode.InvalidText,
                $"{nomeDoCampo} é obrigatório.");
        }

        var texto = valor.Trim();

        return texto.Length > tamanhoMaximo
            ? throw DomainException.For(
                DomainErrorCode.InvalidText,
                $"{nomeDoCampo} aceita no máximo {tamanhoMaximo} caracteres. "
                + $"Recebido: {texto.Length}.")
            : texto;
    }

    private static string? TextoOpcional(string? valor, int tamanhoMaximo, string nomeDoCampo)
    {
        if (string.IsNullOrWhiteSpace(valor))
        {
            return null;
        }

        var texto = valor.Trim();

        return texto.Length > tamanhoMaximo
            ? throw DomainException.For(
                DomainErrorCode.InvalidText,
                $"{nomeDoCampo} aceita no máximo {tamanhoMaximo} caracteres. "
                + $"Recebido: {texto.Length}.")
            : texto;
    }
}
