using Ledger.Domain;
using Microsoft.EntityFrameworkCore;

namespace Ledger.Infrastructure.Persistence;

/// <summary>
/// Contexto de persistência do razão.
/// </summary>
/// <remarks>
/// <para>
/// <b>O rastreamento de mudanças não é usado.</b> O caminho de escrita deste
/// sistema é <c>INSERT</c> — o ledger é append-only, nada é alterado —, e o
/// caminho de leitura é agregação. Change tracking existe para detectar
/// alterações em entidades carregadas, e aqui não há alteração a detectar: ele
/// só custaria memória e tempo comparando instantâneos que nunca divergem.
/// </para>
/// <para>
/// Daí <see cref="QueryTrackingBehavior.NoTracking"/> como padrão. O EF Core fica
/// usado por duas coisas que ele faz bem e que valem a dependência: migrations
/// versionadas e mapeamento tipado do esquema.
/// </para>
/// <para>
/// Os nomes de tabela e coluna são declarados explicitamente, em
/// <c>snake_case</c>, em lugar de derivados por convenção. É mais código e vale
/// a pena: o esquema de um razão contábil é consultado à mão durante incidente,
/// e nome inferido por convenção muda quando a convenção muda.
/// </para>
/// </remarks>
public sealed class LedgerDbContext(DbContextOptions<LedgerDbContext> options) : DbContext(options)
{
    /// <summary>Contas.</summary>
    public DbSet<AccountRecord> Accounts => Set<AccountRecord>();

    /// <summary>Movimentações. Tabela append-only.</summary>
    public DbSet<MovementRecord> Movements => Set<MovementRecord>();

    /// <summary>Chaves de idempotência. É aqui que vive a garantia de unicidade.</summary>
    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();

    /// <summary>Cache de saldo de fechamento. Descartável.</summary>
    public DbSet<BalanceSnapshotRecord> BalanceSnapshots => Set<BalanceSnapshotRecord>();

    /// <inheritdoc/>
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);
        optionsBuilder.UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking);
    }

    /// <inheritdoc/>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        ConfigurarContas(modelBuilder);
        ConfigurarMovimentacoes(modelBuilder);
        ConfigurarIdempotencia(modelBuilder);
        ConfigurarSnapshots(modelBuilder);
    }

    private static void ConfigurarContas(ModelBuilder modelBuilder) =>
        modelBuilder.Entity<AccountRecord>(conta =>
        {
            conta.ToTable("accounts");
            conta.HasKey(c => c.Id).HasName("pk_accounts");

            conta.Property(c => c.Id).HasColumnName("id");
            conta.Property(c => c.CustomerId).HasColumnName("customer_id");

            conta.Property(c => c.Currency)
                .HasColumnName("currency")
                .HasColumnType("char(3)")
                .IsRequired();

            // Situação armazenada como texto, não como inteiro. Um inteiro
            // exigiria consultar o código-fonte para saber o que é "2" durante
            // um incidente; o custo é alguns bytes por linha.
            conta.Property(c => c.Status)
                .HasColumnName("status")
                .HasConversion<string>()
                .HasMaxLength(16)
                .IsRequired();

            conta.Property(c => c.Version)
                .HasColumnName("version")
                .HasDefaultValue(0L);

            // Localiza as contas de um cliente sem varrer a tabela.
            conta.HasIndex(c => c.CustomerId).HasDatabaseName("ix_accounts_customer_id");
        });

    private static void ConfigurarMovimentacoes(ModelBuilder modelBuilder) =>
        modelBuilder.Entity<MovementRecord>(movimentacao =>
        {
            movimentacao.ToTable("movements");
            movimentacao.HasKey(m => m.Id).HasName("pk_movements");

            movimentacao.Property(m => m.Id).HasColumnName("id");
            movimentacao.Property(m => m.AccountId).HasColumnName("account_id");

            movimentacao.Property(m => m.Type)
                .HasColumnName("type")
                .HasConversion<string>()
                .HasMaxLength(8)
                .IsRequired();

            // numeric(19,4): decimal exato, sem perda na ida nem na volta.
            // Quatro casas porque existe fração de centavo em taxa e rateio.
            movimentacao.Property(m => m.Amount)
                .HasColumnName("amount")
                .HasColumnType("numeric(19,4)")
                .IsRequired();

            movimentacao.Property(m => m.Currency)
                .HasColumnName("currency")
                .HasColumnType("char(3)")
                .IsRequired();

            movimentacao.Property(m => m.OccurredAt)
                .HasColumnName("occurred_at")
                .HasColumnType("timestamptz")
                .IsRequired();

            movimentacao.Property(m => m.PostedAt)
                .HasColumnName("posted_at")
                .HasColumnType("timestamptz")
                .IsRequired();

            movimentacao.Property(m => m.IdempotencyKey)
                .HasColumnName("idempotency_key")
                .HasMaxLength(IdempotencyKey.MaxLength)
                .IsRequired();

            movimentacao.Property(m => m.ExternalReference)
                .HasColumnName("external_reference")
                .HasMaxLength(Movement.MaxExternalReferenceLength);

            movimentacao.Property(m => m.CorrelationId)
                .HasColumnName("correlation_id")
                .HasMaxLength(Movement.MaxCorrelationIdLength)
                .IsRequired();

            // O índice que serve a consulta de saldo e o extrato: dado uma conta,
            // percorrer a janela de tempo. É a razão de a consulta temporal não
            // varrer a tabela.
            movimentacao.HasIndex(m => new { m.AccountId, m.OccurredAt })
                .HasDatabaseName("ix_movements_account_id_occurred_at");

            // Rastreabilidade: localizar a movimentação a partir da chave, sem
            // unique — a unicidade vive em idempotency_records (ADR-0012).
            movimentacao.HasIndex(m => m.IdempotencyKey)
                .HasDatabaseName("ix_movements_idempotency_key");

            movimentacao.HasOne<AccountRecord>()
                .WithMany()
                .HasForeignKey(m => m.AccountId)
                .HasConstraintName("fk_movements_account")
                .OnDelete(DeleteBehavior.Restrict);

            // Invariante no esquema, e não apenas no domínio: valor sempre
            // positivo, com o sinal dado pelo tipo. Um caminho de código que
            // ignore o domínio ainda é barrado aqui.
            movimentacao.ToTable(t => t.HasCheckConstraint(
                "ck_movements_amount_positive",
                "amount > 0"));
        });

    private static void ConfigurarIdempotencia(ModelBuilder modelBuilder) =>
        modelBuilder.Entity<IdempotencyRecord>(registro =>
        {
            registro.ToTable("idempotency_records");

            // A chave é a própria chave primária: unicidade garantida pelo
            // esquema, no único lugar que não mente sob concorrência.
            registro.HasKey(r => r.Key).HasName("pk_idempotency_records");

            registro.Property(r => r.Key)
                .HasColumnName("key")
                .HasMaxLength(IdempotencyKey.MaxLength)
                .IsRequired();

            registro.Property(r => r.AccountId).HasColumnName("account_id");
            registro.Property(r => r.MovementId).HasColumnName("movement_id");

            registro.Property(r => r.RequestFingerprint)
                .HasColumnName("request_fingerprint")
                .HasMaxLength(64)
                .IsRequired();

            registro.Property(r => r.CreatedAt)
                .HasColumnName("created_at")
                .HasColumnType("timestamptz")
                .IsRequired();

            registro.Property(r => r.ExpiresAt)
                .HasColumnName("expires_at")
                .HasColumnType("timestamptz")
                .IsRequired();

            // Serve a limpeza das chaves expiradas sem varredura.
            registro.HasIndex(r => r.ExpiresAt)
                .HasDatabaseName("ix_idempotency_records_expires_at");
        });

    private static void ConfigurarSnapshots(ModelBuilder modelBuilder) =>
        modelBuilder.Entity<BalanceSnapshotRecord>(snapshot =>
        {
            snapshot.ToTable("balance_snapshots");
            snapshot.HasKey(s => new { s.AccountId, s.AsOfDate })
                .HasName("pk_balance_snapshots");

            snapshot.Property(s => s.AccountId).HasColumnName("account_id");

            snapshot.Property(s => s.AsOfDate)
                .HasColumnName("as_of_date")
                .HasColumnType("date")
                .IsRequired();

            snapshot.Property(s => s.ClosingBalance)
                .HasColumnName("closing_balance")
                .HasColumnType("numeric(19,4)")
                .IsRequired();

            snapshot.Property(s => s.Currency)
                .HasColumnName("currency")
                .HasColumnType("char(3)")
                .IsRequired();

            snapshot.Property(s => s.MovementCount)
                .HasColumnName("movement_count")
                .IsRequired();

            snapshot.Property(s => s.IsStale)
                .HasColumnName("is_stale")
                .HasDefaultValue(false)
                .IsRequired();

            snapshot.HasOne<AccountRecord>()
                .WithMany()
                .HasForeignKey(s => s.AccountId)
                .HasConstraintName("fk_balance_snapshots_account")
                .OnDelete(DeleteBehavior.Cascade);
        });
}
