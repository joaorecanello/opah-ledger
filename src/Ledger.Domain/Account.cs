namespace Ledger.Domain;

/// <summary>
/// Conta de um cliente. É por ela que uma movimentação entra no razão.
/// </summary>
/// <remarks>
/// <para>
/// <b>A conta não tem saldo.</b> Essa ausência é a decisão central da solução.
/// Saldo armazenado como campo mutável, sob concorrência, perde atualização sem
/// erro visível; protegido por lock, torna-se um ponto de serialização por conta
/// que empilha conexões no pico. Aqui o saldo é função sobre as movimentações, e
/// escrever é apenas inserir um fato — dois fatos não disputam recurso nenhum.
/// </para>
/// <para>
/// <b>A conta também não tem a coleção de movimentações.</b> Carregar cinco anos
/// de histórico para registrar um lançamento seria inviável. O que a conta faz é
/// guardar os invariantes que governam o registro — moeda e situação — e ser a
/// porta de entrada obrigatória por onde a movimentação é criada. É um agregado
/// que protege regras sem materializar o histórico que elas governam.
/// </para>
/// <para>
/// A consequência prática: registrar uma movimentação exige carregar a conta. É
/// uma leitura a mais, e ela paga por si: sem a conta, nada impediria registrar
/// em moeda errada ou numa conta encerrada.
/// </para>
/// </remarks>
public sealed class Account
{
    private Account(AccountId id, CustomerId customerId, Currency currency, AccountStatus status)
    {
        Id = id;
        CustomerId = customerId;
        Currency = currency;
        Status = status;
    }

    /// <summary>Identificador da conta.</summary>
    public AccountId Id { get; }

    /// <summary>Cliente titular, por referência opaca.</summary>
    public CustomerId CustomerId { get; }

    /// <summary>Moeda em que a conta opera.</summary>
    public Currency Currency { get; }

    /// <summary>Situação da conta quanto ao registro de movimentações.</summary>
    public AccountStatus Status { get; }

    /// <summary>Abre uma conta nova, ativa.</summary>
    /// <exception cref="DomainException">Algum identificador ou a moeda está ausente.</exception>
    public static Account Open(AccountId id, CustomerId customerId, Currency currency)
    {
        Validar(id, customerId, currency);
        return new Account(id, customerId, currency, AccountStatus.Active);
    }

    /// <summary>Reconstrói uma conta já persistida.</summary>
    /// <exception cref="DomainException">
    /// Algum identificador está ausente, a moeda não foi informada, ou a situação
    /// não é reconhecida.
    /// </exception>
    public static Account Rehydrate(
        AccountId id,
        CustomerId customerId,
        Currency currency,
        AccountStatus status)
    {
        Validar(id, customerId, currency);

        return status is AccountStatus.Active or AccountStatus.Blocked or AccountStatus.Closed
            ? new Account(id, customerId, currency, status)
            : throw DomainException.For(
                DomainErrorCode.InvalidText,
                $"Situação de conta não reconhecida: {(int)status}.");
    }

    /// <summary>
    /// Cria a movimentação a ser registrada nesta conta.
    /// </summary>
    /// <param name="type">Crédito ou débito.</param>
    /// <param name="amount">Quantia, sempre positiva, na moeda da conta.</param>
    /// <param name="occurredAt">Quando o fato aconteceu (tempo de negócio).</param>
    /// <param name="postedAt">Quando o sistema registra (tempo de sistema, do servidor).</param>
    /// <param name="idempotencyKey">Chave que torna o registro seguro para repetição.</param>
    /// <param name="correlationId">Correlação da requisição que originou o registro.</param>
    /// <param name="externalReference">Referência no sistema de origem, opcional.</param>
    /// <returns>A movimentação pronta para ser persistida.</returns>
    /// <remarks>
    /// <b>Não há verificação de saldo.</b> Este sistema registra o fato
    /// consumado; a decisão de autorizar o débito pertence a quem origina a
    /// movimentação. Ver PR-03 em docs/premissas.md — é a premissa de maior
    /// impacto no desenho, e se ela cair surge um invariante por conta que muda
    /// o tratamento de concorrência.
    /// </remarks>
    /// <exception cref="DomainException">
    /// A conta está encerrada, a moeda não corresponde à da conta, a quantia não
    /// é positiva, ou algum campo obrigatório está ausente.
    /// </exception>
    public Movement Register(
        MovementType type,
        Money amount,
        DateTimeOffset occurredAt,
        DateTimeOffset postedAt,
        IdempotencyKey idempotencyKey,
        string correlationId,
        string? externalReference = null)
    {
        if (Status == AccountStatus.Closed)
        {
            throw DomainException.For(
                DomainErrorCode.AccountClosed,
                $"Conta {Id} está encerrada e não aceita novas movimentações.");
        }

        // Conta bloqueada aceita registro de propósito: bloqueio impede
        // originar operação, não registrar fato que já aconteceu. Ver PR-13.
        return Movement.Create(
            Id,
            Currency,
            type,
            amount,
            occurredAt,
            postedAt,
            idempotencyKey,
            correlationId,
            externalReference);
    }

    private static void Validar(AccountId id, CustomerId customerId, Currency currency)
    {
        if (id.IsUnset)
        {
            throw DomainException.For(
                DomainErrorCode.MissingIdentifier,
                "Identificador de conta não informado.");
        }

        if (customerId.IsUnset)
        {
            throw DomainException.For(
                DomainErrorCode.MissingIdentifier,
                "Identificador de cliente não informado.");
        }

        if (currency.IsUnset)
        {
            throw DomainException.For(
                DomainErrorCode.MissingCurrency,
                "Conta exige moeda.");
        }
    }
}
