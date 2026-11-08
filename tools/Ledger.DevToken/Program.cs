using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

// Emite um JWT de desenvolvimento.
//
//   dotnet run --project tools/Ledger.DevToken -- [opções]
//
//   --scopes   ledger:read,ledger:write   (padrão: os dois)
//   --customer <guid>                     restringe o token às contas de um cliente
//   --cross-customer                      acesso a contas de qualquer cliente
//   --minutes  60                         validade (padrão 60)
//
// A chave, o emissor e o destinatário vêm das mesmas variáveis que a API lê, com
// os mesmos padrões do docker-compose.yml. Os padrões são públicos: servem a
// ambiente local e a nada mais.
const string DefaultKey = "bGVkZ2VyLWRldi1vbmx5LWtleS1uZXZlci11c2UtaW4tcHJvZCEh";

var argumentos = args.ToList();

string? Valor(string nome)
{
    var i = argumentos.IndexOf(nome);
    return i >= 0 && i + 1 < argumentos.Count ? argumentos[i + 1] : null;
}

var escopos = (Valor("--scopes") ?? "ledger:read,ledger:write")
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    .ToList();

var cliente = Valor("--customer");
var irrestrito = argumentos.Contains("--cross-customer");

if (irrestrito && !escopos.Contains("ledger:cross-customer"))
{
    escopos.Add("ledger:cross-customer");
}

if (cliente is null && !irrestrito)
{
    Console.Error.WriteLine(
        "Informe --customer <guid> ou --cross-customer: um token sem alcance definido é recusado pela API.");
    return 2;
}

if (cliente is not null && !Guid.TryParse(cliente, out _))
{
    Console.Error.WriteLine("--customer precisa ser um GUID.");
    return 2;
}

var minutos = int.TryParse(Valor("--minutes"), out var m) && m > 0 ? m : 60;

var chave = Environment.GetEnvironmentVariable("Ledger__Auth__SigningKey") ?? DefaultKey;
var emissor = Environment.GetEnvironmentVariable("Ledger__Auth__Issuer") ?? "ledger-dev";
var destinatario = Environment.GetEnvironmentVariable("Ledger__Auth__Audience") ?? "ledger-api";

var claims = new Dictionary<string, object>
{
    ["sub"] = "dev-client",
    ["scope"] = string.Join(' ', escopos),
};

if (cliente is not null)
{
    claims["customer_id"] = cliente;
}

var descritor = new SecurityTokenDescriptor
{
    Issuer = emissor,
    Audience = destinatario,
    Expires = DateTime.UtcNow.AddMinutes(minutos),
    Claims = claims,
    SigningCredentials = new SigningCredentials(
        new SymmetricSecurityKey(Convert.FromBase64String(chave)),
        SecurityAlgorithms.HmacSha256),
};

Console.WriteLine(new JsonWebTokenHandler().CreateToken(descritor));
return 0;
