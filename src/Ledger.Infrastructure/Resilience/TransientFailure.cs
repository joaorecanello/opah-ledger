using System.Net.Sockets;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Ledger.Infrastructure.Resilience;

/// <summary>
/// Define o que é uma falha transitória de dependência.
/// </summary>
/// <remarks>
/// <para>
/// A definição vive num só lugar porque três mecanismos precisam concordar: o
/// retry do acesso a dados, o circuit breaker e o mapeamento para <c>503</c>.
/// Se divergirem, o sistema repete uma falha que o breaker ignora, ou responde
/// <c>503</c> a um erro que não é de dependência — e <c>503</c> diz ao chamador
/// "repita", o que seria mentira para um defeito de código.
/// </para>
/// <para>
/// <b>O que entra:</b> o banco não responde, a conexão caiu, o tempo esgotou. São
/// condições que somem sozinhas e para as quais repetir é a resposta certa.
/// </para>
/// <para>
/// <b>O que não entra:</b> violação de constraint, erro de sintaxe, dado
/// inválido. Essas são determinísticas — repetir devolve o mesmo erro — e
/// escondê-las atrás de um <c>503</c> mandaria quem integra esperar por algo que
/// nunca vai se resolver. Elas continuam sendo <c>500</c>, que é o que são:
/// defeito nosso.
/// </para>
/// </remarks>
public static class TransientFailure
{
    /// <summary>Indica se a exceção, ou algo dentro dela, é uma falha transitória.</summary>
    public static bool IsTransient(Exception? exception)
    {
        // Percorre a cadeia inteira: o EF embrulha a falha do driver, e o retry
        // do EF embrulha de novo. A causa que interessa costuma estar duas ou
        // três camadas abaixo da exceção que chega ao tratador.
        for (var atual = exception; atual is not null; atual = atual.InnerException)
        {
            if (atual is NpgsqlException { IsTransient: true }
                or RetryLimitExceededException
                or TimeoutException
                or SocketException
                or IOException)
            {
                return true;
            }

            // Falha de DNS ao resolver o host do banco. Acontece de verdade
            // quando o container do banco está parado e a rede do compose deixa
            // de resolver o nome — foi como o ledger-db parado se apresentou.
            if (atual is NpgsqlException && atual.InnerException is SocketException)
            {
                return true;
            }
        }

        return false;
    }
}
