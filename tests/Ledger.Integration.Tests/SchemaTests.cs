namespace Ledger.Integration.Tests;

/// <summary>
/// Verifica o esquema como o PostgreSQL de fato o criou.
/// </summary>
/// <remarks>
/// Não verifica a configuração do EF Core — verifica o resultado dela no banco.
/// A diferença importa: a configuração pode estar escrita e não produzir o
/// índice ou a constraint pretendidos, e aí o erro só apareceria como lentidão
/// em produção ou como duplicata que passou.
/// <para>
/// Que as migrations aplicam é verificado pelo próprio fixture: ele as aplica
/// contra um banco vazio em toda execução da suíte.
/// </para>
/// </remarks>
[Collection(PostgresDatabase.Name)]
public sealed class SchemaTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Cria_todas_as_tabelas_esperadas()
    {
        fixture.SkipIfUnavailable();
        await using var contexto = fixture.CreateContext();

        var tabelas = await LedgerTestData.ConsultarCatalogoAsync(
            contexto,
            """
            SELECT tablename FROM pg_tables
            WHERE schemaname = 'public'
            ORDER BY tablename
            """);

        tabelas.ShouldBe(
            ["__migrations", "accounts", "balance_snapshots", "idempotency_records", "movements"]);
    }

    [Fact]
    public async Task Valor_monetario_e_numeric_19_4_e_nunca_ponto_flutuante()
    {
        fixture.SkipIfUnavailable();
        await using var contexto = fixture.CreateContext();

        var colunas = await LedgerTestData.ConsultarCatalogoAsync(
            contexto,
            """
            SELECT table_name || '.' || column_name || ' = ' || data_type ||
                   '(' || numeric_precision || ',' || numeric_scale || ')'
            FROM information_schema.columns
            WHERE table_schema = 'public'
              AND column_name IN ('amount', 'closing_balance')
            ORDER BY table_name
            """);

        colunas.ShouldBe([
            "balance_snapshots.closing_balance = numeric(19,4)",
            "movements.amount = numeric(19,4)",
        ]);
    }

    [Fact]
    public async Task Instantes_sao_timestamptz()
    {
        fixture.SkipIfUnavailable();
        await using var contexto = fixture.CreateContext();

        var tipos = await LedgerTestData.ConsultarCatalogoAsync(
            contexto,
            """
            SELECT DISTINCT data_type
            FROM information_schema.columns
            WHERE table_schema = 'public'
              AND column_name IN ('occurred_at', 'posted_at', 'created_at', 'expires_at')
            """);

        tipos.ShouldBe(["timestamp with time zone"]);
    }

    [Fact]
    public async Task Indice_que_serve_a_consulta_de_saldo_existe()
    {
        // Sem este índice, a consulta temporal varre a tabela — e é justamente
        // a varredura que torna o sistema lento conforme o histórico cresce.
        fixture.SkipIfUnavailable();
        await using var contexto = fixture.CreateContext();

        var indices = await LedgerTestData.ConsultarCatalogoAsync(
            contexto,
            """
            SELECT indexname FROM pg_indexes
            WHERE schemaname = 'public' AND tablename = 'movements'
            ORDER BY indexname
            """);

        indices.ShouldContain("ix_movements_account_id_occurred_at");
    }

    [Fact]
    public async Task Unicidade_da_chave_de_idempotencia_esta_em_tabela_propria()
    {
        // ADR-0012: a unicidade NÃO está em movements. Está na chave primária de
        // idempotency_records, que não é particionada — o PostgreSQL exige que o
        // unique constraint de uma tabela particionada inclua a coluna de
        // particionamento, e um unique em (chave, data) permitiria a mesma chave
        // com datas diferentes, ou seja, permitiria a duplicata.
        fixture.SkipIfUnavailable();
        await using var contexto = fixture.CreateContext();

        var unicosEmMovements = await LedgerTestData.ConsultarCatalogoAsync(
            contexto,
            """
            SELECT indexname FROM pg_indexes
            WHERE schemaname = 'public' AND tablename = 'movements'
              AND indexdef LIKE '%UNIQUE%'
            """);

        unicosEmMovements.ShouldNotContain("ix_movements_idempotency_key");

        var chavePrimaria = await LedgerTestData.ConsultarCatalogoAsync(
            contexto,
            """
            SELECT conname FROM pg_constraint
            WHERE conrelid = 'idempotency_records'::regclass AND contype = 'p'
            """);

        chavePrimaria.ShouldBe(["pk_idempotency_records"]);
    }

    [Fact]
    public async Task Esquema_impede_valor_nao_positivo()
    {
        fixture.SkipIfUnavailable();
        await using var contexto = fixture.CreateContext();

        var checks = await LedgerTestData.ConsultarCatalogoAsync(
            contexto,
            """
            SELECT conname FROM pg_constraint
            WHERE conrelid = 'movements'::regclass AND contype = 'c'
            ORDER BY conname
            """);

        checks.ShouldContain("ck_movements_amount_positive");
    }

    [Fact]
    public async Task Nao_existe_coluna_de_saldo_em_lugar_nenhum()
    {
        // A ausência de saldo mutável é a decisão central da solução (ADR-0002),
        // e ela precisa valer no esquema e não só no domínio: uma coluna de
        // saldo em accounts seria exatamente o ponto de contenção que o desenho
        // existe para eliminar.
        //
        // balance_snapshots tem closing_balance, que é outra coisa: cache de
        // período fechado, descartável e reconstruível.
        fixture.SkipIfUnavailable();
        await using var contexto = fixture.CreateContext();

        var colunasDeSaldo = await LedgerTestData.ConsultarCatalogoAsync(
            contexto,
            """
            SELECT table_name || '.' || column_name
            FROM information_schema.columns
            WHERE table_schema = 'public'
              AND table_name <> 'balance_snapshots'
              AND (column_name LIKE '%balance%' OR column_name LIKE '%saldo%')
            """);

        colunasDeSaldo.ShouldBeEmpty();
    }
}
