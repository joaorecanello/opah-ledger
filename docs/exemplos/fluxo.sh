#!/usr/bin/env bash
# Percorre os endpoints da Ledger API de ponta a ponta e mostra cada resposta.
#
#   docker compose up --build        # em outro terminal
#   bash docs/exemplos/fluxo.sh      # na raiz do repositório
#
# Variáveis opcionais:
#   HOST   endereço da API         (padrão http://localhost:5080)
#   TOKEN  token JWT já emitido    (padrão: emite um com tools/Ledger.DevToken)
#
# Precisa de: curl e, para emitir o token, o SDK .NET 10 (ou defina TOKEN).
# A chave de assinatura do ambiente local é pública; isto não serve a produção.

set -u

HOST="${HOST:-http://localhost:5080}"
RODADA="$(date +%s)-$RANDOM"
CLIENTE="$(cat /proc/sys/kernel/random/uuid 2>/dev/null || printf '2f1b8d4e-0c7a-4a1f-9b3e-%012d' "$RANDOM")"

emitir() { dotnet run --project tools/Ledger.DevToken -c Release -- "$@"; }

if [ -z "${TOKEN:-}" ]; then
  echo ">> emitindo token (use TOKEN=... para pular)"
  TOKEN="$(emitir --cross-customer)" || { echo "falha ao emitir o token"; exit 1; }
fi

passo() { printf '\n\033[1m== %s\033[0m\n' "$1"; }

# chamar <esperado> <método> <caminho> [curl-args...]
# Mostra o status e o corpo, e avisa se o status não for o esperado.
chamar() {
  local esperado="$1" metodo="$2" caminho="$3"; shift 3
  local saida status corpo
  saida="$(curl -sS -o - -w '\n%{http_code}' -X "$metodo" "$HOST$caminho" "$@")" || { echo "  ✗ API inalcançável em $HOST"; exit 1; }
  status="${saida##*$'\n'}"
  corpo="${saida%$'\n'*}"
  if [ "$status" = "$esperado" ]; then marca="✓"; else marca="✗ ESPERADO $esperado"; fi
  printf '  %s %s %s\n  %s\n' "$marca" "$status" "$metodo $caminho" "$corpo"
  ULTIMO_CORPO="$corpo"
}

json() { printf '%s' "$ULTIMO_CORPO" | sed -n "s/.*\"$1\":\"\\([^\"]*\\)\".*/\\1/p"; }

AUTH=(-H "Authorization: Bearer $TOKEN")
JSON=(-H 'Content-Type: application/json')

passo "0. Saúde (pública)"
chamar 200 GET /health/live
chamar 200 GET /health/ready

passo "1. Sem token"
chamar 401 POST /api/v1/accounts "${JSON[@]}" -d "{\"customerId\":\"$CLIENTE\",\"currency\":\"BRL\"}"

passo "2. Abrir conta"
chamar 201 POST /api/v1/accounts "${AUTH[@]}" "${JSON[@]}" -d "{\"customerId\":\"$CLIENTE\",\"currency\":\"BRL\"}"
CONTA="$(json accountId)"
echo "  conta: $CONTA"
[ -n "$CONTA" ] || { echo "sem conta; abortando"; exit 1; }

passo "3. Crédito de 1.500,00"
CORPO_CREDITO='{"type":"Credit","amount":1500.00,"currency":"BRL","occurredAt":"2026-11-07T13:00:00Z","externalReference":"PIX-0001"}'
chamar 201 POST "/api/v1/accounts/$CONTA/movements" "${AUTH[@]}" "${JSON[@]}" -H "Idempotency-Key: ex-$RODADA-1" -d "$CORPO_CREDITO"
MOVIMENTO="$(json movementId)"

passo "4. Repetição idêntica (retry): 200 e o MESMO movimento, nada novo gravado"
chamar 200 POST "/api/v1/accounts/$CONTA/movements" "${AUTH[@]}" "${JSON[@]}" -H "Idempotency-Key: ex-$RODADA-1" -d "$CORPO_CREDITO"

passo "5. Mesma chave, valor diferente: 409"
chamar 409 POST "/api/v1/accounts/$CONTA/movements" "${AUTH[@]}" "${JSON[@]}" -H "Idempotency-Key: ex-$RODADA-1" \
  -d '{"type":"Credit","amount":9999.00,"currency":"BRL","occurredAt":"2026-11-07T13:00:00Z","externalReference":"PIX-0001"}'

passo "6. Débito de 400,25"
chamar 201 POST "/api/v1/accounts/$CONTA/movements" "${AUTH[@]}" "${JSON[@]}" -H "Idempotency-Key: ex-$RODADA-2" \
  -d '{"type":"Debit","amount":400.25,"currency":"BRL","occurredAt":"2026-11-08T09:30:00Z"}'

passo "7. Erros de validação"
chamar 400 POST "/api/v1/accounts/$CONTA/movements" "${AUTH[@]}" "${JSON[@]}" \
  -d '{"type":"Credit","amount":10,"currency":"BRL","occurredAt":"2026-11-08T09:30:00Z"}'
chamar 400 POST "/api/v1/accounts/$CONTA/movements" "${AUTH[@]}" "${JSON[@]}" -H "Idempotency-Key: ex-$RODADA-z" \
  -d '{"type":"Credit","amount":0,"currency":"BRL","occurredAt":"2026-11-08T09:30:00Z"}'
chamar 400 POST "/api/v1/accounts/$CONTA/movements" "${AUTH[@]}" "${JSON[@]}" -H "Idempotency-Key: ex-$RODADA-t" \
  -d '{"type":"Transfer","amount":10,"currency":"BRL","occurredAt":"2026-11-08T09:30:00Z"}'

passo "8. Saldo agora (esperado 1099.75) e no passado"
chamar 200 GET "/api/v1/accounts/$CONTA/balance" "${AUTH[@]}"
chamar 200 GET "/api/v1/accounts/$CONTA/balance?asOf=2026-11-08T00:00:00Z" "${AUTH[@]}"
chamar 200 GET "/api/v1/accounts/$CONTA/balance?asOf=2026-01-01T00:00:00Z" "${AUTH[@]}"
chamar 400 GET "/api/v1/accounts/$CONTA/balance?asOf=2099-01-01T00:00:00Z" "${AUTH[@]}"

passo "9. Buscar a movimentação"
chamar 200 GET "/api/v1/accounts/$CONTA/movements/$MOVIMENTO" "${AUTH[@]}"

passo "10. Autorização"
SO_LEITURA="$(emitir --scopes ledger:read --cross-customer 2>/dev/null)"
OUTRO="$(emitir --customer "$(cat /proc/sys/kernel/random/uuid 2>/dev/null || echo 7b6e0c1a-2f3d-4a5b-8c9d-0e1f2a3b4c5d)" 2>/dev/null)"
if [ -n "$SO_LEITURA" ] && [ -n "$OUTRO" ]; then
  echo "  token só de leitura tentando escrever:"
  chamar 403 POST "/api/v1/accounts/$CONTA/movements" -H "Authorization: Bearer $SO_LEITURA" "${JSON[@]}" -H "Idempotency-Key: ex-$RODADA-403" \
    -d '{"type":"Credit","amount":10,"currency":"BRL","occurredAt":"2026-11-08T09:30:00Z"}'
  echo "  token de OUTRO cliente lendo esta conta (404, igual a conta inexistente):"
  chamar 404 GET "/api/v1/accounts/$CONTA/balance" -H "Authorization: Bearer $OUTRO"
else
  echo "  (pulado: sem o SDK .NET não há como emitir tokens extras)"
fi
echo "  token adulterado:"
chamar 401 GET "/api/v1/accounts/$CONTA/balance" -H "Authorization: Bearer eyJhbGciOiJIUzI1NiJ9.e30.assinatura-falsa"

passo "Pronto. Interface interativa: $HOST/swagger"
