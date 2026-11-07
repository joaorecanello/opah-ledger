namespace Ledger.Domain;

/// <summary>Natureza da movimentação.</summary>
/// <remarks>
/// Começa em 1, e não em 0, de propósito: assim o valor padrão de uma variável
/// não inicializada não é um tipo válido de movimentação. Um crédito acidental
/// por esquecimento de atribuição deixa de ser possível.
/// </remarks>
public enum MovementType
{
    /// <summary>Entrada de valor na conta.</summary>
    Credit = 1,

    /// <summary>Saída de valor da conta.</summary>
    Debit = 2,
}

/// <summary>Situação da conta quanto ao registro de movimentações.</summary>
/// <remarks>
/// A distinção entre <see cref="Blocked"/> e <see cref="Closed"/> é uma decisão
/// de domínio, não uma formalidade. Ver a premissa PR-13 em docs/premissas.md.
/// </remarks>
public enum AccountStatus
{
    /// <summary>Valor não atribuído. Nunca deve ocorrer.</summary>
    Unspecified = 0,

    /// <summary>Conta em operação normal.</summary>
    Active = 1,

    /// <summary>
    /// Conta bloqueada para originar operações, mas que ainda aceita registro.
    /// </summary>
    /// <remarks>
    /// Bloqueio impede <b>originar</b> movimentação, não <b>registrar</b> fato
    /// que já aconteceu. Um estorno de uma operação anterior, ou um lançamento
    /// em trânsito quando o bloqueio entrou em vigor, precisa entrar no razão —
    /// recusá-lo faria a contabilidade divergir da realidade.
    /// </remarks>
    Blocked = 2,

    /// <summary>Conta encerrada. Não aceita novas movimentações.</summary>
    /// <remarks>
    /// Ao contrário do bloqueio, o encerramento é definitivo: a conta não existe
    /// mais para fins operacionais, e um lançamento nela indica erro de
    /// roteamento na origem.
    /// </remarks>
    Closed = 3,
}
