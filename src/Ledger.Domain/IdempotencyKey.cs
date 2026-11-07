namespace Ledger.Domain;

/// <summary>
/// Chave de idempotência fornecida por quem registra a movimentação.
/// </summary>
/// <remarks>
/// <para>
/// É <b>obrigatória</b> no registro de movimentação, e isso é contrato, não
/// detalhe de implementação. Retry é o comportamento normal de qualquer cliente
/// com timeout configurado — ou seja, de todo cliente correto. Sem a chave, uma
/// queda de rede entre o commit e a resposta duplica o lançamento, e duplicata
/// é pior que indisponibilidade: é invisível até a conciliação.
/// </para>
/// <para>
/// Chave opcional é chave ausente na hora em que importa, porque algum cliente
/// vai omiti-la e o primeiro incidente de rede vai duplicar os lançamentos
/// dele. Daí o tipo não admitir ausência.
/// </para>
/// <para>
/// A garantia de unicidade propriamente dita não vive aqui: vive no índice
/// único do banco, que é o único lugar que não mente sob concorrência. Este
/// tipo apenas garante que a chave tem forma utilizável.
/// </para>
/// </remarks>
public readonly record struct IdempotencyKey
{
    /// <summary>Tamanho máximo aceito, em caracteres.</summary>
    public const int MaxLength = 128;

    /// <summary>Tamanho mínimo aceito, em caracteres.</summary>
    public const int MinLength = 8;

    private IdempotencyKey(string value) => Value = value;

    /// <summary>Texto da chave.</summary>
    public string Value { get; }

    /// <summary>A instância é o valor padrão e não contém chave alguma.</summary>
    public bool IsUnset => string.IsNullOrEmpty(Value);

    /// <summary>Valida e cria a chave.</summary>
    /// <param name="value">Texto fornecido por quem chama.</param>
    /// <exception cref="DomainException">A chave está ausente ou fora do formato.</exception>
    public static IdempotencyKey Of(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw DomainException.For(
                DomainErrorCode.InvalidIdempotencyKey,
                "Chave de idempotência é obrigatória no registro de movimentação.");
        }

        var chave = value.Trim();

        if (chave.Length is < MinLength or > MaxLength)
        {
            throw DomainException.For(
                DomainErrorCode.InvalidIdempotencyKey,
                $"Chave de idempotência deve ter entre {MinLength} e {MaxLength} "
                + $"caracteres. Recebido: {chave.Length}.");
        }

        foreach (var caractere in chave)
        {
            // ASCII imprimível, sem espaço. A chave viaja em header HTTP e vira
            // valor de índice único: espaço e caractere de controle produziriam
            // duas chaves que parecem iguais no log e são diferentes no banco,
            // que é exatamente a confusão que a idempotência existe para evitar.
            if (caractere is < '!' or > '~')
            {
                throw DomainException.For(
                    DomainErrorCode.InvalidIdempotencyKey,
                    "Chave de idempotência aceita apenas ASCII imprimível sem espaço.");
            }
        }

        return new IdempotencyKey(chave);
    }

    /// <inheritdoc/>
    public override string ToString() => Value;
}
