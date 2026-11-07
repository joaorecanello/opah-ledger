using System.Reflection;

namespace Ledger.Infrastructure;

/// <summary>
/// Marcador de assembly da infraestrutura.
/// </summary>
/// <remarks>
/// Existe para dar aos testes de arquitetura um ponto de entrada estável por
/// camada, sem depender de nome de assembly em string — que silenciosamente
/// para de encontrar o assembly se alguém renomear o projeto, fazendo o teste
/// passar sem verificar nada.
/// </remarks>
public static class InfrastructureAssembly
{
    /// <summary>Assembly desta camada.</summary>
    public static Assembly Reference => typeof(InfrastructureAssembly).Assembly;
}
