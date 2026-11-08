# Imagem do worker do razão.
#
# Contexto de build: a RAIZ do repositório, não esta pasta. Mesmo motivo do
# Dockerfile da API — o projeto depende de Directory.Build.props,
# Directory.Packages.props e global.json, que vivem na raiz.

# ---------------------------------------------------------------------------
# Compilação
# ---------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src

COPY global.json Directory.Build.props Directory.Packages.props ./
COPY tests/Directory.Build.props tests/
COPY src/Ledger.Domain/Ledger.Domain.csproj src/Ledger.Domain/
COPY src/Ledger.Application/Ledger.Application.csproj src/Ledger.Application/
COPY src/Ledger.Infrastructure/Ledger.Infrastructure.csproj src/Ledger.Infrastructure/
COPY src/Ledger.Worker/Ledger.Worker.csproj src/Ledger.Worker/

RUN dotnet restore src/Ledger.Worker/Ledger.Worker.csproj

COPY .editorconfig ./
COPY src/ src/

RUN dotnet publish src/Ledger.Worker/Ledger.Worker.csproj \
    --configuration Release \
    -warnaserror \
    --no-restore \
    --output /app

# ---------------------------------------------------------------------------
# Execução
# ---------------------------------------------------------------------------
# Runtime sem ASP.NET: este processo não serve HTTP. A imagem fica menor e a
# superfície exposta também — um job em lote não tem motivo para carregar um
# servidor web.
FROM mcr.microsoft.com/dotnet/runtime:10.0-alpine AS final
WORKDIR /app

USER $APP_UID

ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1

COPY --from=build /app ./

ENTRYPOINT ["dotnet", "Ledger.Worker.dll"]
