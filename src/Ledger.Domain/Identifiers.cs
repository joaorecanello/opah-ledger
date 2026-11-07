namespace Ledger.Domain;

/// <summary>Identificador de conta.</summary>
/// <remarks>
/// Identificador tipado em lugar de <see cref="Guid"/> solto: impede a classe
/// de bug em que uma conta e um cliente são trocados de posição numa chamada —
/// o compilador recusa, em vez de o sistema registrar a movimentação na conta
/// errada.
/// </remarks>
public readonly record struct AccountId
{
    private AccountId(Guid value) => Value = value;

    /// <summary>Valor subjacente.</summary>
    public Guid Value { get; }

    /// <summary>A instância é o valor padrão e não identifica conta alguma.</summary>
    public bool IsUnset => Value == Guid.Empty;

    /// <summary>Gera um identificador novo.</summary>
    public static AccountId New() => new(Guid.CreateVersion7());

    /// <summary>Reconstrói o identificador a partir do valor persistido.</summary>
    /// <exception cref="DomainException">O valor é vazio.</exception>
    public static AccountId From(Guid value) =>
        value == Guid.Empty
            ? throw DomainException.For(
                DomainErrorCode.MissingIdentifier,
                "Identificador de conta não informado.")
            : new AccountId(value);

    /// <inheritdoc/>
    public override string ToString() => Value.ToString();
}

/// <summary>Identificador de cliente.</summary>
/// <remarks>
/// Opaco de propósito: o razão contábil não guarda dado pessoal, apenas a
/// referência. O nome, o CPF e o endereço vivem no contexto de Clientes.
/// Minimização é a primeira camada de proteção de dados, antes de criptografia
/// ou controle de acesso.
/// </remarks>
public readonly record struct CustomerId
{
    private CustomerId(Guid value) => Value = value;

    /// <summary>Valor subjacente.</summary>
    public Guid Value { get; }

    /// <summary>A instância é o valor padrão e não identifica cliente algum.</summary>
    public bool IsUnset => Value == Guid.Empty;

    /// <summary>Gera um identificador novo.</summary>
    public static CustomerId New() => new(Guid.CreateVersion7());

    /// <summary>Reconstrói o identificador a partir do valor persistido.</summary>
    /// <exception cref="DomainException">O valor é vazio.</exception>
    public static CustomerId From(Guid value) =>
        value == Guid.Empty
            ? throw DomainException.For(
                DomainErrorCode.MissingIdentifier,
                "Identificador de cliente não informado.")
            : new CustomerId(value);

    /// <inheritdoc/>
    public override string ToString() => Value.ToString();
}

/// <summary>Identificador de movimentação.</summary>
/// <remarks>
/// <para>
/// Usa UUID versão 7 (RFC 9562): os 48 bits mais significativos são o instante
/// de criação em milissegundos, logo o identificador é <b>ordenável por
/// tempo</b>. Isso dá localidade de índice na escrita — inserções consecutivas
/// caem na mesma página do índice, em lugar de espalhar por toda a árvore como
/// um UUID v4 faria.
/// </para>
/// <para>
/// E, ao contrário de um inteiro sequencial exposto, não revela volume: um
/// terceiro que receba dois identificadores não consegue inferir quantas
/// movimentações o banco processou entre eles.
/// </para>
/// <para>
/// A escolha original tinha sido ULID, que dá as mesmas duas propriedades. UUID
/// v7 venceu por um motivo concreto deste projeto: está na biblioteca base do
/// .NET, e <c>Ledger.Domain</c> tem a regra de dependência zero verificada por
/// teste de arquitetura. ULID exigiria um pacote externo no domínio.
/// </para>
/// </remarks>
public readonly record struct MovementId
{
    private MovementId(Guid value) => Value = value;

    /// <summary>Valor subjacente.</summary>
    public Guid Value { get; }

    /// <summary>A instância é o valor padrão e não identifica movimentação alguma.</summary>
    public bool IsUnset => Value == Guid.Empty;

    /// <summary>
    /// Gera um identificador ordenável pelo instante informado.
    /// </summary>
    /// <param name="at">
    /// Instante de registro. Recebido como parâmetro, e não lido do relógio do
    /// sistema aqui dentro, para que o domínio permaneça determinístico e
    /// testável: quem chama controla o tempo.
    /// </param>
    public static MovementId NewAt(DateTimeOffset at) => new(Guid.CreateVersion7(at));

    /// <summary>Reconstrói o identificador a partir do valor persistido.</summary>
    /// <exception cref="DomainException">O valor é vazio.</exception>
    public static MovementId From(Guid value) =>
        value == Guid.Empty
            ? throw DomainException.For(
                DomainErrorCode.MissingIdentifier,
                "Identificador de movimentação não informado.")
            : new MovementId(value);

    /// <inheritdoc/>
    public override string ToString() => Value.ToString();
}
