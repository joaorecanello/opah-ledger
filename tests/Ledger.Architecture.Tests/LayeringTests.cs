using Ledger.Application;
using Ledger.Domain;
using NetArchTest.Rules;

namespace Ledger.Architecture.Tests;

/// <summary>
/// Trava as fronteiras de camada do ADR-0001 (monólito modular).
/// </summary>
/// <remarks>
/// A tese do ADR-0001 é que microsserviços não se justificam aqui, mas que as
/// fronteiras de módulo precisam ser reais para que a extração de um serviço
/// seja mecânica no dia em que houver razão para ela. "Reais" significa
/// verificadas no build: sem isto, um <c>using</c> conveniente corrói o desenho
/// em poucas sprints e ninguém percebe até a extração ficar caro.
/// </remarks>
public sealed class LayeringTests
{
    private const string Application = "Ledger.Application";
    private const string Infrastructure = "Ledger.Infrastructure";
    private const string Api = "Ledger.Api";
    private const string Worker = "Ledger.Worker";

    [Fact]
    public void Dominio_nao_depende_de_nenhuma_outra_camada()
    {
        var result = Types.InAssembly(DomainAssembly.Reference)
            .ShouldNot()
            .HaveDependencyOnAny(Application, Infrastructure, Api, Worker)
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(Describe(result));
    }

    [Fact]
    public void Dominio_nao_depende_de_infraestrutura_tecnica()
    {
        // O domínio não conhece banco, HTTP, serialização nem injeção de
        // dependência. Se um conceito precisa de algum deles, está na camada
        // errada.
        var result = Types.InAssembly(DomainAssembly.Reference)
            .ShouldNot()
            .HaveDependencyOnAny(
                "Microsoft.EntityFrameworkCore",
                "Microsoft.AspNetCore",
                "Microsoft.Extensions.DependencyInjection",
                "Npgsql",
                "System.Data",
                "System.Net.Http",
                "System.Text.Json")
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(Describe(result));
    }

    [Fact]
    public void Casos_de_uso_nao_dependem_de_infraestrutura_nem_de_hosts()
    {
        // Inversão de dependência: Application declara as portas,
        // Infrastructure as implementa. A seta nunca aponta de volta.
        var result = Types.InAssembly(ApplicationAssembly.Reference)
            .ShouldNot()
            .HaveDependencyOnAny(Infrastructure, Api, Worker)
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(Describe(result));
    }

    [Fact]
    public void Casos_de_uso_nao_dependem_de_tecnologia_de_persistencia_ou_web()
    {
        var result = Types.InAssembly(ApplicationAssembly.Reference)
            .ShouldNot()
            .HaveDependencyOnAny(
                "Microsoft.EntityFrameworkCore",
                "Microsoft.AspNetCore",
                "Npgsql")
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(Describe(result));
    }

    [Fact]
    public void Dominio_nao_referencia_nenhum_pacote_externo()
    {
        // Complementa as regras acima: o NetArchTest olha dependência entre
        // tipos; isto olha o grafo de assemblies. Pega o caso de alguém
        // adicionar uma PackageReference ao domínio "só para resolver rápido".
        var externas = DomainAssembly.Reference
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .Where(nome => !nome.StartsWith("System", StringComparison.Ordinal)
                        && !nome.Equals("netstandard", StringComparison.Ordinal)
                        && !nome.Equals("mscorlib", StringComparison.Ordinal))
            .OrderBy(nome => nome, StringComparer.Ordinal)
            .ToArray();

        externas.ShouldBeEmpty(
            $"Ledger.Domain deve ter dependência zero. Encontrado: {string.Join(", ", externas)}");
    }

    private static string Describe(NetArchTest.Rules.TestResult result)
    {
        var nomes = result.FailingTypeNames ?? [];
        return nomes.Count == 0
            ? "Regra de arquitetura violada."
            : $"Tipos que violam a regra: {string.Join(", ", nomes)}";
    }
}
