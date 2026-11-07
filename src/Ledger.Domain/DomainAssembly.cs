using System.Reflection;

namespace Ledger.Domain;

/// <summary>
/// Marcador de assembly de dominio.
/// </summary>
/// <remarks>
/// Existe para dar aos testes de arquitetura um ponto de entrada estavel por
/// camada, sem depender de nome de assembly em string (que silenciosamente
/// para de encontrar o assembly se alguem renomear o projeto, fazendo o teste
/// passar sem verificar nada).
/// </remarks>
public static class DomainAssembly
{
    /// <summary>Assembly desta camada.</summary>
    public static Assembly Reference => typeof(DomainAssembly).Assembly;
}
