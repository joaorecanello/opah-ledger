using Ledger.Domain;

namespace Ledger.Infrastructure.Persistence;

/// <summary>
/// Linha da tabela <c>accounts</c>.
/// </summary>
/// <remarks>
/// <para>
/// Modelo de persistência separado do modelo de domínio, mapeado explicitamente.
/// O domínio é estritamente imutável, com construtor privado e objetos de valor,
/// e um ORM precisa de tipos que ele consiga materializar. Ou o domínio cede —
/// propriedades com <c>set</c>, construtor sem argumentos — ou a persistência
/// ganha tipos próprios. Esta é a segunda opção, e a razão está no
/// ADR-0013.
/// </para>
/// <para>
/// Note que <b>não há coluna de saldo</b>. A ausência é a decisão central da
/// solução (ADR-0002), e ela se reflete aqui: nem o domínio nem o esquema têm
/// onde guardar um saldo mutável.
/// </para>
/// </remarks>
public sealed class AccountRecord
{
    /// <summary>Identificador da conta.</summary>
    public Guid Id { get; set; }

    /// <summary>Cliente titular, por referência opaca.</summary>
    public Guid CustomerId { get; set; }

    /// <summary>Código ISO 4217 da moeda da conta.</summary>
    public string Currency { get; set; } = string.Empty;

    /// <summary>Situação da conta.</summary>
    public AccountStatus Status { get; set; }

    /// <summary>
    /// Reservado para concorrência otimística por conta, sem uso no momento.
    /// </summary>
    /// <remarks>
    /// Existe porque habilitar a sequência contígua por conta do ADR-0007, ou um
    /// invariante de saldo caso PR-03 caia, exigiria esta coluna — e adicionar
    /// coluna em tabela grande depois é migração custosa. Reservá-la agora custa
    /// 8 bytes por conta.
    /// </remarks>
    public long Version { get; set; }
}

/// <summary>
/// Linha da tabela <c>movements</c>: um fato imutável do razão.
/// </summary>
/// <remarks>
/// A tabela é append-only. Não há caminho de código que emita <c>UPDATE</c> ou
/// <c>DELETE</c> aqui, e correção de erro é movimentação compensatória que
/// referencia a original.
/// </remarks>
public sealed class MovementRecord
{
    /// <summary>Identificador da movimentação (UUID v7, ordenável por tempo).</summary>
    public Guid Id { get; set; }

    /// <summary>Conta movimentada.</summary>
    public Guid AccountId { get; set; }

    /// <summary>Crédito ou débito.</summary>
    public MovementType Type { get; set; }

    /// <summary>
    /// Quantia, sempre positiva. Armazenada em <c>numeric(19,4)</c>.
    /// </summary>
    public decimal Amount { get; set; }

    /// <summary>Código ISO 4217 da moeda.</summary>
    public string Currency { get; set; } = string.Empty;

    /// <summary>Quando o fato aconteceu (tempo de negócio).</summary>
    public DateTimeOffset OccurredAt { get; set; }

    /// <summary>Quando o sistema registrou (tempo de sistema).</summary>
    public DateTimeOffset PostedAt { get; set; }

    /// <summary>
    /// Chave de idempotência da requisição que originou o registro.
    /// </summary>
    /// <remarks>
    /// <b>Esta coluna não tem unique constraint.</b> A garantia de unicidade vive
    /// na tabela <c>idempotency_records</c>, que não é particionada — ver
    /// ADR-0012. Aqui a coluna existe para rastreabilidade e para reconstruir a
    /// movimentação de domínio, que carrega a chave.
    /// </remarks>
    public string IdempotencyKey { get; set; } = string.Empty;

    /// <summary>Referência no sistema de origem, quando houver.</summary>
    public string? ExternalReference { get; set; }

    /// <summary>Correlação da requisição que originou o registro.</summary>
    public string CorrelationId { get; set; } = string.Empty;
}

/// <summary>
/// Linha da tabela <c>idempotency_records</c>.
/// </summary>
/// <remarks>
/// <para>
/// É aqui que a garantia de não-duplicidade de fato vive: o unique constraint
/// sobre <see cref="Key"/>. A inserção acontece na mesma transação da
/// movimentação, logo ou as duas entram ou nenhuma entra.
/// </para>
/// <para>
/// Tabela própria, e não uma coluna em <c>movements</c>, porque o PostgreSQL
/// exige que o unique constraint de uma tabela particionada inclua a coluna de
/// particionamento. Com <c>movements</c> particionada por mês, um unique em
/// <c>(idempotency_key, occurred_at)</c> permitiria a mesma chave com datas
/// diferentes — ou seja, permitiria a duplicata que a chave existe para
/// impedir. Ver ADR-0012.
/// </para>
/// </remarks>
public sealed class IdempotencyRecord
{
    /// <summary>Chave fornecida por quem registrou. Unique.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Conta a que o registro se refere.</summary>
    public Guid AccountId { get; set; }

    /// <summary>Movimentação resultante da primeira tentativa.</summary>
    public Guid MovementId { get; set; }

    /// <summary>
    /// Impressão do conteúdo da requisição original.
    /// </summary>
    /// <remarks>
    /// Permite distinguir repetição legítima — mesma chave, mesmo conteúdo, que
    /// devolve a resposta original — de erro do chamador: mesma chave com
    /// conteúdo diferente, que devolve <c>409</c>. Adivinhar a intenção em
    /// sistema financeiro é inaceitável, e sem isto não haveria como saber qual
    /// dos dois casos é.
    /// </remarks>
    public string RequestFingerprint { get; set; } = string.Empty;

    /// <summary>Quando o registro foi criado.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// Quando o registro pode ser removido pela limpeza.
    /// </summary>
    /// <remarks>
    /// A janela cobre folgadamente qualquer retry plausível. Sem expiração, a
    /// tabela cresceria para sempre guardando chaves que ninguém vai repetir.
    /// </remarks>
    public DateTimeOffset ExpiresAt { get; set; }
}

/// <summary>
/// Linha da tabela <c>balance_snapshots</c>: cache de saldo de fechamento.
/// </summary>
/// <remarks>
/// Esta tabela pode ser truncada por inteiro a qualquer momento, e o worker a
/// reconstrói a partir do ledger. É cache, nunca fonte da verdade — e é essa
/// propriedade que torna a otimização aceitável em dados financeiros
/// (ADR-0004).
/// </remarks>
public sealed class BalanceSnapshotRecord
{
    /// <summary>Conta a que o snapshot se refere.</summary>
    public Guid AccountId { get; set; }

    /// <summary>Dia encerrado que o snapshot consolida.</summary>
    public DateOnly AsOfDate { get; set; }

    /// <summary>Saldo ao fim do dia, em <c>numeric(19,4)</c>.</summary>
    public decimal ClosingBalance { get; set; }

    /// <summary>Código ISO 4217 da moeda.</summary>
    public string Currency { get; set; } = string.Empty;

    /// <summary>Movimentações consideradas até o fechamento.</summary>
    public long MovementCount { get; set; }

    /// <summary>Invalidado por movimentação retroativa.</summary>
    public bool IsStale { get; set; }
}
