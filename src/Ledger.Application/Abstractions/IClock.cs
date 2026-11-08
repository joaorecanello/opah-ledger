namespace Ledger.Application.Abstractions;

/// <summary>
/// Fonte do tempo de sistema.
/// </summary>
/// <remarks>
/// <para>
/// Existe por duas razões, e nenhuma delas é purismo.
/// </para>
/// <para>
/// A primeira é testabilidade: o instante de registro entra no identificador da
/// movimentação e na janela de validação temporal. Sem controle do relógio,
/// testar "movimentação datada 31 dias atrás é recusada" exigiria datas
/// calculadas a partir de <c>DateTimeOffset.UtcNow</c>, e o teste mudaria de
/// comportamento conforme o dia em que roda.
/// </para>
/// <para>
/// A segunda é que o instante de registro é <b>decisão do servidor</b>, nunca do
/// cliente. Tê-lo atrás de uma porta deixa explícito de onde ele vem, em lugar
/// de espalhar chamadas ao relógio pelo caso de uso.
/// </para>
/// </remarks>
public interface IClock
{
    /// <summary>Instante atual em UTC.</summary>
    /// <remarks>
    /// A implementação deve devolver um instante com precisão que o
    /// armazenamento consiga guardar sem perda. O motivo é concreto: a resposta
    /// de um replay vem do banco, e a do registro original vem daqui — se as
    /// duas não forem idênticas, o mesmo lançamento ganha dois carimbos
    /// diferentes, que é a divergência que a idempotência existe para evitar.
    /// </remarks>
    DateTimeOffset UtcNow { get; }
}
