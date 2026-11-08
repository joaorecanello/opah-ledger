# Imagem da API do razão.
#
# Contexto de build: a RAIZ do repositório, não esta pasta. O projeto depende de
# Directory.Build.props, Directory.Packages.props e global.json, que vivem na
# raiz — sem eles a imagem compilaria com outras versões e outras regras de
# analisador, e deixaria de ser a mesma coisa que o CI verifica.

# ---------------------------------------------------------------------------
# Compilação
# ---------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src

# Primeiro só o que define o grafo de dependências. Enquanto nenhum .csproj
# mudar, a camada de restore é reaproveitada e o build de uma alteração de
# código não baixa pacote nenhum.
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY tests/Directory.Build.props tests/
COPY src/Ledger.Domain/Ledger.Domain.csproj src/Ledger.Domain/
COPY src/Ledger.Application/Ledger.Application.csproj src/Ledger.Application/
COPY src/Ledger.Infrastructure/Ledger.Infrastructure.csproj src/Ledger.Infrastructure/
COPY src/Ledger.Api/Ledger.Api.csproj src/Ledger.Api/

RUN dotnet restore src/Ledger.Api/Ledger.Api.csproj

# Agora o código.
COPY .editorconfig ./
COPY src/ src/

# -warnaserror é redundante com o TreatWarningsAsErrors do
# Directory.Build.props, e está aqui pelo mesmo motivo que está no CI: se
# alguém relaxar a propriedade, a imagem continua recusando compilar fora do
# padrão. O requisito de compilar sem warnings vale em todo lugar que compila.
RUN dotnet publish src/Ledger.Api/Ledger.Api.csproj \
    --configuration Release \
    -warnaserror \
    --no-restore \
    --output /app

# ---------------------------------------------------------------------------
# Execução
# ---------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS final
WORKDIR /app

# Usuário sem privilégio, que a imagem base já traz. Processo de aplicação não
# tem motivo para rodar como root, e num sistema financeiro essa é a diferença
# entre uma falha contida e um comprometimento do host.
USER $APP_UID

# A imagem Alpine não traz ICU, e o projeto usa InvariantGlobalization. Isso é
# compatível de propósito: o razão formata valores em cultura invariante, porque
# a vírgula decimal depender do locale do servidor seria uma forma silenciosa de
# errar dinheiro.
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 \
    ASPNETCORE_HTTP_PORTS=8080

EXPOSE 8080

COPY --from=build /app ./

ENTRYPOINT ["dotnet", "Ledger.Api.dll"]
