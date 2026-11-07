using System.Reflection;

namespace Ledger.Application;

/// <summary>
/// Marcador de assembly dos casos de uso.
/// </summary>
/// <remarks>
/// Existe para dar aos testes de arquitetura um ponto de entrada estável por
/// camada, sem depender de nome de assembly em string — que silenciosamente
/// para de encontrar o assembly se alguém renomear o projeto, fazendo o teste
/// passar sem verificar nada.
/// </remarks>
public static class ApplicationAssembly
{
    /// <summary>Assembly desta camada.</summary>
    public static Assembly Reference => typeof(ApplicationAssembly).Assembly;
}
