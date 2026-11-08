using Ledger.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;

namespace Ledger.Infrastructure;

/// <summary>Resolve a connection string do razão a partir da configuração.</summary>
/// <remarks>
/// <para>
/// A resolução acontece <b>quando o contexto é construído</b>, não quando os
/// serviços são registrados. A diferença parece sutil e não é: a configuração
/// final do host só existe depois do <c>Build</c>, e qualquer fonte adicionada
/// pelo pipeline — um teste apontando para o próprio banco, um provedor de
/// segredos — é invisível para quem leu a configuração no registro.
/// </para>
/// <para>
/// Isto apareceu ao escrever os testes de API: eles sobrepunham a connection
/// string e a aplicação continuava indo ao banco do <c>appsettings</c>, porque o
/// valor já havia sido lido.
/// </para>
/// </remarks>
public static class LedgerConnectionString
{
    /// <summary>Nome da seção no <c>appsettings</c>.</summary>
    public const string ConfigurationKey = "Ledger";

    /// <summary>Resolve a connection string.</summary>
    /// <param name="configuration">Configuração do host.</param>
    /// <returns>A connection string do PostgreSQL.</returns>
    /// <exception cref="InvalidOperationException">Não está configurada.</exception>
    /// <remarks>
    /// A variável de ambiente tem precedência sobre o <c>appsettings</c>: é como
    /// um container recebe configuração, e evita que um valor de
    /// desenvolvimento versionado vença o do ambiente por acidente.
    /// </remarks>
    public static string Resolve(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var daVariavel = configuration[LedgerDbContextFactory.ConnectionStringVariable];

        if (!string.IsNullOrWhiteSpace(daVariavel))
        {
            return daVariavel;
        }

        var doArquivo = configuration.GetConnectionString(ConfigurationKey);

        return !string.IsNullOrWhiteSpace(doArquivo)
            ? doArquivo
            : throw new InvalidOperationException(
                "Connection string do PostgreSQL não configurada. Defina a variável "
                + $"{LedgerDbContextFactory.ConnectionStringVariable} ou "
                + $"ConnectionStrings:{ConfigurationKey} no appsettings.");
    }
}
