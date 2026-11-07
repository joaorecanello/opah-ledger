using System.Reflection;

namespace Ledger.Domain.Tests;

/// <summary>
/// Verifica que os tipos do domínio são imutáveis.
/// </summary>
/// <remarks>
/// <para>
/// O ledger é append-only: o saldo nunca muda, ele só se acumula. Isso só vale
/// se os tipos não permitirem alteração — e "não permitirem" precisa ser
/// verificado, porque adicionar um <c>set</c> é uma linha que passa em revisão
/// de código sem ninguém notar que o modelo de consistência mudou.
/// </para>
/// <para>
/// É também o que torna a trilha de auditoria confiável: não existe a pergunta
/// "quem alterou este lançamento", porque nenhum lançamento pode ser alterado.
/// </para>
/// </remarks>
public sealed class ImmutabilityTests
{
    public static TheoryData<Type> TiposDoDominio =>
        new()
        {
            typeof(Movement),
            typeof(Account),
            typeof(BalanceSnapshot),
            typeof(Money),
            typeof(Currency),
            typeof(IdempotencyKey),
            typeof(AccountId),
            typeof(CustomerId),
            typeof(MovementId),
        };

    [Theory]
    [MemberData(nameof(TiposDoDominio))]
    public void Tipo_do_dominio_nao_tem_propriedade_alteravel(Type tipo)
    {
        var alteraveis = tipo
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.SetMethod is not null && !p.SetMethod.IsPrivate)
            .Select(p => p.Name)
            .ToArray();

        alteraveis.ShouldBeEmpty(
            $"{tipo.Name} expõe propriedade alterável: {string.Join(", ", alteraveis)}. "
            + "O ledger é append-only; fato registrado não muda.");
    }

    [Theory]
    [MemberData(nameof(TiposDoDominio))]
    public void Tipo_do_dominio_nao_tem_campo_de_instancia_publico(Type tipo)
    {
        var campos = tipo
            .GetFields(BindingFlags.Public | BindingFlags.Instance)
            .Where(f => !f.IsInitOnly)
            .Select(f => f.Name)
            .ToArray();

        campos.ShouldBeEmpty($"{tipo.Name} expõe campo público mutável: {string.Join(", ", campos)}.");
    }

    [Fact]
    public void Marcar_snapshot_como_obsoleto_devolve_nova_instancia()
    {
        var original = BalanceSnapshot.Create(
            AccountId.New(),
            new DateOnly(2026, 11, 4),
            Money.Of(12_500m, Currency.Brl),
            movementCount: 1_842);

        var marcado = original.MarkStale();

        original.IsStale.ShouldBeFalse("a instância original não deve ser alterada");
        marcado.IsStale.ShouldBeTrue();
        marcado.ShouldNotBeSameAs(original);

        // Marcar e não apagar é deliberado: o valor antigo continua disponível
        // para diagnosticar quanto a retroatividade mudou o histórico.
        marcado.ClosingBalance.ShouldBe(original.ClosingBalance);
        marcado.MovementCount.ShouldBe(original.MovementCount);
    }

    [Fact]
    public void Movimentacao_nao_tem_metodo_que_altere_estado()
    {
        var metodosSuspeitos = typeof(Movement)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.Name.StartsWith("Set", StringComparison.Ordinal)
                     || m.Name.StartsWith("Update", StringComparison.Ordinal)
                     || m.Name.StartsWith("Change", StringComparison.Ordinal)
                     || m.Name.StartsWith("Alterar", StringComparison.Ordinal))
            .Select(m => m.Name)
            .ToArray();

        metodosSuspeitos.ShouldBeEmpty(
            "Correção de erro é movimentação compensatória que referencia a "
            + "original, nunca alteração do registro.");
    }
}
