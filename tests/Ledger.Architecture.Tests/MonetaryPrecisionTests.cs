using System.Reflection;
using System.Runtime.CompilerServices;
using Ledger.Application;
using Ledger.Domain;

namespace Ledger.Architecture.Tests;

/// <summary>
/// Proíbe ponto flutuante binário no domínio e nos casos de uso.
/// </summary>
/// <remarks>
/// <para>
/// <c>double</c> e <c>float</c> são base 2 e não representam 0,1 exatamente.
/// Somar centavos acumula erro, e num razão contábil erro acumulado é dinheiro
/// que não fecha. O tipo monetário do projeto é <c>decimal</c> (base 10),
/// mapeado para <c>numeric(19,4)</c> no PostgreSQL.
/// Ver docs/adr/0005-persistencia-postgresql.md.
/// </para>
/// <para>
/// Este teste é uma rede, não uma formalidade: a forma mais provável de o bug
/// entrar não é alguém declarar <c>double Saldo</c>, é alguém escrever
/// <c>var total = 0.0;</c> num cálculo auxiliar, onde nenhuma revisão de código
/// repara. Hoje ele passa trivialmente porque o domínio ainda está vazio; o
/// valor dele começa quando houver código.
/// </para>
/// </remarks>
public sealed class MonetaryPrecisionTests
{
    private static readonly Type[] TiposProibidos =
    [
        typeof(double),
        typeof(float),
        typeof(double?),
        typeof(float?),
    ];

    public static TheoryData<string, Assembly> CamadasDeNegocio =>
        new()
        {
            { "Ledger.Domain", DomainAssembly.Reference },
            { "Ledger.Application", ApplicationAssembly.Reference },
        };

    [Theory]
    [MemberData(nameof(CamadasDeNegocio))]
    public void Camada_de_negocio_nao_usa_ponto_flutuante_binario(string camada, Assembly assembly)
    {
        var ocorrencias = EncontrarUsosDeFloat(assembly).ToArray();

        ocorrencias.ShouldBeEmpty(
            $"""
            {camada} usa ponto flutuante binário em valores que podem ser monetários.
            Use decimal. Ocorrências:
              {string.Join("\n  ", ocorrencias)}
            """);
    }

    private static IEnumerable<string> EncontrarUsosDeFloat(Assembly assembly)
    {
        const BindingFlags Todos =
            BindingFlags.Public | BindingFlags.NonPublic |
            BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        foreach (var tipo in assembly.GetTypes().Where(t => !EhGeradoPeloCompilador(t)))
        {
            foreach (var campo in tipo.GetFields(Todos).Where(f => Proibido(f.FieldType)))
            {
                yield return $"{tipo.FullName}.{campo.Name} (campo {campo.FieldType.Name})";
            }

            foreach (var prop in tipo.GetProperties(Todos).Where(p => Proibido(p.PropertyType)))
            {
                yield return $"{tipo.FullName}.{prop.Name} (propriedade {prop.PropertyType.Name})";
            }

            foreach (var metodo in tipo.GetMethods(Todos))
            {
                if (Proibido(metodo.ReturnType))
                {
                    yield return $"{tipo.FullName}.{metodo.Name} (retorno {metodo.ReturnType.Name})";
                }

                foreach (var p in metodo.GetParameters().Where(p => Proibido(p.ParameterType)))
                {
                    yield return $"{tipo.FullName}.{metodo.Name} (parâmetro {p.Name}: {p.ParameterType.Name})";
                }
            }
        }
    }

    private static bool Proibido(Type tipo) => Array.Exists(TiposProibidos, t => t == tipo);

    private static bool EhGeradoPeloCompilador(Type tipo) =>
        tipo.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false)
        || (tipo.FullName?.Contains('<', StringComparison.Ordinal) ?? false);
}
