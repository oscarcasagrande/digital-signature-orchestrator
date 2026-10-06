#!/usr/bin/env bash
# Smoke test against a running docker compose (default http://localhost:8080).
# Usage: bash scripts/smoke-test.sh [base-url]
set -euo pipefail

BASE="${1:-http://localhost:8080}"
SAMPLE="$(dirname "$0")/../specs/001-core-orchestration/contracts/sample-request.json"
fail() { echo "FAIL: $*" >&2; exit 1; }
field() { sed -n "s/.*\"$1\":\"\([^\"]*\)\".*/\1/p" | head -1; }

echo "== health"
for i in $(seq 1 60); do
  [ "$(curl -s -o /dev/null -w '%{http_code}' "$BASE/health")" = "200" ] && break
  sleep 2
  [ "$i" = 60 ] && fail "API not healthy"
done

KC="${KEYCLOAK_URL:-http://localhost:8180}/realms/orchestrator/protocol/openid-connect/token"
echo "== identity provider (Keycloak) and tokens"
for i in $(seq 1 90); do
  [ "$(command curl -s -o /dev/null -w '%{http_code}' "${KEYCLOAK_URL:-http://localhost:8180}/realms/orchestrator/.well-known/openid-configuration")" = "200" ] && break
  sleep 2
  [ "$i" = 90 ] && fail "Keycloak not ready"
done
token_of() { sed -n 's/.*"access_token":"\([^"]*\)".*/\1/p' | head -1; }
user_token() { command curl -s -X POST "$KC" -d grant_type=password -d client_id=orchestrator-cli -d "username=$1" -d "password=$1" | token_of; }
client_token() { command curl -s -X POST "$KC" -d grant_type=client_credentials -d "client_id=$1" -d "client_secret=$2" | token_of; }
TOKEN_ADMIN=$(user_token admin); TOKEN_OPERATOR=$(user_token operator); TOKEN_VIEWER=$(user_token viewer)
TOKEN_A=$(client_token orchestrator-client-a demo-secret-client-a-change-me); TOKEN_B=$(client_token orchestrator-client-b demo-secret-client-b-change-me)
for t in "$TOKEN_ADMIN" "$TOKEN_OPERATOR" "$TOKEN_VIEWER" "$TOKEN_A" "$TOKEN_B"; do [ -n "$t" ] || fail "could not obtain a token"; done
# The earlier checks run as admin (all roles); the security section below uses each identity explicitly.
TOKEN="$TOKEN_ADMIN"
curl() { command curl -H "Authorization: Bearer $TOKEN" "$@"; }

KEY="smoke-$(date +%s)-$RANDOM"
echo "== create (idempotency key $KEY)"
R1=$(curl -s -D /tmp/h1 -o /tmp/b1 -w '%{http_code}' -X POST "$BASE/v1/signature-processes" \
  -H "Idempotency-Key: $KEY" -H "Content-Type: application/json" --data @"$SAMPLE")
[ "$R1" = "202" ] || fail "expected 202, got $R1: $(cat /tmp/b1)"
ID=$(field processId < /tmp/b1)
[ -n "$ID" ] || fail "no processId"
echo "processId=$ID"

echo "== replay with same key and payload"
R2=$(curl -s -o /tmp/b2 -w '%{http_code}' -X POST "$BASE/v1/signature-processes" \
  -H "Idempotency-Key: $KEY" -H "Content-Type: application/json" --data @"$SAMPLE")
[ "$R2" = "200" ] || fail "expected 200 on replay, got $R2"
[ "$(field processId < /tmp/b2)" = "$ID" ] || fail "replay returned a different process"

echo "== same key, different payload"
R3=$(curl -s -o /dev/null -w '%{http_code}' -X POST "$BASE/v1/signature-processes" \
  -H "Idempotency-Key: $KEY" -H "Content-Type: application/json" --data '{"externalId":"OTHER"}')
[ "$R3" = "409" ] || [ "$R3" = "400" ] || fail "expected conflict/validation, got $R3"

echo "== missing key"
R4=$(curl -s -o /dev/null -w '%{http_code}' -X POST "$BASE/v1/signature-processes" -H "Content-Type: application/json" --data @"$SAMPLE")
[ "$R4" = "400" ] || fail "expected 400 without key, got $R4"

echo "== wait for COMPLETED"
for i in $(seq 1 60); do
  ST=$(curl -s "$BASE/v1/signature-processes/$ID/status" | field businessStatus)
  [ "$ST" = "COMPLETED" ] && break
  sleep 1
  [ "$i" = 60 ] && fail "process did not complete (last status: $ST)"
done
echo "business=COMPLETED"

echo "== operations and events"
OPS=$(curl -s "$BASE/v1/signature-processes/$ID/operations")
for t in DOCUMENT_DOWNLOAD DOCUMENT_STORE PROVIDER_CREATE_PROCESS PROVIDER_SEND_DOCUMENT PROVIDER_STATUS_CHECK SIGNED_DOCUMENT_DOWNLOAD SIGNED_DOCUMENT_STORE; do
  echo "$OPS" | grep -q "\"$t\"" || fail "operation $t missing"
done
EVS=$(curl -s "$BASE/v1/signature-processes/$ID/events")
for t in PROCESS_CREATED SIGNATURE_COMPLETED PROCESS_COMPLETED; do
  echo "$EVS" | grep -q "\"$t\"" || fail "event $t missing"
done

echo "== artifacts (original, signed, evidence)"
ARTS=$(curl -s "$BASE/v1/signature-processes/$ID/artifacts" | sed 's/},{/}\n{/g')
art_sha() { echo "$ARTS" | grep "\"type\":\"$1\"" | sed -n 's/.*"sha256":"\([^"]*\)".*/\1/p' | head -1; }
ORIG_SHA=$(art_sha ORIGINAL_DOCUMENT); SIGNED_SHA=$(art_sha SIGNED_DOCUMENT); EVID_SHA=$(art_sha EVIDENCE)
[ -n "$ORIG_SHA" ] && [ -n "$SIGNED_SHA" ] && [ -n "$EVID_SHA" ] || fail "missing artifacts: $ARTS"
[ "$ORIG_SHA" != "$SIGNED_SHA" ] || fail "signed document equals original"
echo "$ARTS" | grep -qi "storageKey" && fail "storage key leaked in artifacts listing"

echo "== secure download (signed document and original)"
download() { # $1 = request body, prints sha256 of the downloaded content
  local link url
  link=$(curl -s -X POST "$BASE/v1/signature-processes/$ID/download-link" -H "Content-Type: application/json" --data "$1")
  url=$(echo "$link" | field url | sed 's/\u0026/\&/g')
  [ -n "$url" ] || fail "no download url: $link"
  echo "$url" | grep -qiE "minioadmin|signature-artifacts|/input/|/output/" && fail "storage details in url"
  curl -s -o /tmp/dl.bin "$url" || fail "download failed"
  sha256sum /tmp/dl.bin | cut -d' ' -f1
  echo "$url" > /tmp/last-url
}
[ "$(download '{}')" = "$SIGNED_SHA" ] || fail "signed document hash mismatch"
LAST_URL=$(cat /tmp/last-url)
[ "$(download '{"type":"ORIGINAL_DOCUMENT"}')" = "$ORIG_SHA" ] || fail "original document hash mismatch"
TAMPERED=$(curl -s -o /dev/null -w '%{http_code}' "${LAST_URL}x")
[ "$TAMPERED" = "403" ] || fail "tampered link expected 403, got $TAMPERED"
EVS2=$(curl -s "$BASE/v1/signature-processes/$ID/events")
echo "$EVS2" | grep -q '"DOCUMENT_DOWNLOADED"' || fail "DOCUMENT_DOWNLOADED event missing"

echo "== resilience: transient failure recovers automatically"
FL=$(sed "s/CONTRACT-928182/SIM-FLAKY-2-$RANDOM/" "$SAMPLE")
IDF=$(curl -s -X POST "$BASE/v1/signature-processes" -H "Idempotency-Key: smoke-f-$RANDOM-$(date +%s)" -H "Content-Type: application/json" --data "$FL" | field processId)
for i in $(seq 1 60); do
  ST=$(curl -s "$BASE/v1/signature-processes/$IDF/status" | field businessStatus)
  [ "$ST" = "COMPLETED" ] && break
  sleep 1
  [ "$i" = 60 ] && fail "flaky process did not recover (last status: $ST)"
done
curl -s "$BASE/v1/signature-processes/$IDF/events" | grep -q '"OPERATION_RETRY_SCHEDULED"' || fail "retry was not journaled"

echo "== resilience: exhausted retries go to the DLQ, then reprocess"
DW=$(sed "s/CONTRACT-928182/SIM-DOWN-$RANDOM/" "$SAMPLE")
IDD=$(curl -s -X POST "$BASE/v1/signature-processes" -H "Idempotency-Key: smoke-d-$RANDOM-$(date +%s)" -H "Content-Type: application/json" --data "$DW" | field processId)
for i in $(seq 1 60); do
  OS=$(curl -s "$BASE/v1/signature-processes/$IDD/status" | field operationalStatus)
  [ "$OS" = "DLQ" ] && break
  sleep 1
  [ "$i" = 60 ] && fail "process did not reach DLQ (last operational status: $OS)"
done
curl -s "$BASE/v1/dead-letters?domain=signature-provider" | grep -q "$IDD" || fail "dead letter not listed"
RC=$(curl -s -o /dev/null -w '%{http_code}' -X POST "$BASE/v1/signature-processes/$IDD/retry" -H "Content-Type: application/json" --data '{"reason":"smoke"}')
[ "$RC" = "200" ] || fail "retry expected 200, got $RC"
curl -s "$BASE/v1/signature-processes/$IDD/events" | grep -q '"OPERATION_REPROCESS_REQUESTED"' || fail "reprocess not journaled"
RC2=$(curl -s -o /dev/null -w '%{http_code}' -X POST "$BASE/v1/signature-processes/$ID/retry")
[ "$RC2" = "409" ] || fail "retry of completed process expected 409, got $RC2"

echo "== callbacks: signed events delivered to the sink"
for i in $(seq 1 30); do
  SINK=$(curl -s http://localhost:8081/events)
  echo "$SINK" | grep -q "$ID" && echo "$SINK" | grep -q 'SIGNATURE_PROCESS.COMPLETED' && break
  sleep 1
  [ "$i" = 30 ] && fail "sink did not receive the COMPLETED callback"
done
echo "$SINK" | grep -q '"signatureValid":false' && fail "sink reported an invalid signature"
echo "$SINK" | grep -q '"signatureValid":true' || fail "sink did not validate any signature"
DELIV=$(curl -s "$BASE/v1/signature-processes/$ID/callbacks")
echo "$DELIV" | grep -q '"status":"DELIVERED"' || fail "no delivered callback listed: $DELIV"

echo "== callbacks: registered callbackId (secret shown only once)"
CBID="SMOKE_$RANDOM$(date +%s)"
REG=$(curl -s -X POST "$BASE/v1/callbacks" -H "Content-Type: application/json" \
  --data "{\"callbackId\":\"$CBID\",\"url\":\"http://callback-sink:8080/hook\",\"secret\":\"demo-secret-0123456789\"}")
echo "$REG" | grep -q "\"callbackId\":\"$CBID\"" || fail "registration failed: $REG"
curl -s "$BASE/v1/callbacks/$CBID" | grep -qi "secret" && fail "secret leaked by GET /v1/callbacks/{id}"
REGBODY=$(sed "s|\"callback\": { \"url\": \"[^\"]*\" }|\"callback\": { \"callbackId\": \"$CBID\" }|" "$SAMPLE")
IDR=$(curl -s -X POST "$BASE/v1/signature-processes" -H "Idempotency-Key: smoke-r-$RANDOM-$(date +%s)" -H "Content-Type: application/json" --data "$REGBODY" | field processId)
[ -n "$IDR" ] || fail "process with registered callback was not created"
for i in $(seq 1 60); do
  curl -s "$BASE/v1/signature-processes/$IDR/status" | grep -q '"businessStatus":"COMPLETED"' && break
  sleep 1
  [ "$i" = 60 ] && fail "process with registered callback did not complete"
done
for i in $(seq 1 30); do
  curl -s "$BASE/v1/signature-processes/$IDR/callbacks" | grep -q '"eventType":"SIGNATURE_PROCESS.COMPLETED"[^}]*"status":"DELIVERED"' && break
  sleep 1
  [ "$i" = 30 ] && fail "registered callback COMPLETED event was not delivered"
done
UNK=$(curl -s -o /dev/null -w '%{http_code}' -X POST "$BASE/v1/signature-processes" -H "Idempotency-Key: smoke-u-$RANDOM" \
  -H "Content-Type: application/json" --data "$(sed 's|"callback": { "url": "[^"]*" }|"callback": { "callbackId": "NOPE_UNKNOWN" }|' "$SAMPLE")")
[ "$UNK" = "400" ] || fail "unknown callbackId expected 400, got $UNK"

echo "== identity proofing: session without a signature process"
B64() { printf '%s' "$1" | base64 | tr -d '\n'; }
new_session() {
  curl -s -X POST "$BASE/v1/proofing-sessions" -H "Idempotency-Key: smoke-p-$RANDOM-$(date +%s)" -H "Content-Type: application/json" \
    --data "{\"externalId\":\"SMOKE-KYC-$RANDOM\",\"subject\":{\"name\":\"Maria Souza\",\"document\":\"12345678909\"},\"validations\":[\"PERSON_DATA\",\"LIVENESS\",\"FACE_MATCH\"]}" | field sessionId
}
wait_session() {
  for i in $(seq 1 60); do
    R=$(curl -s "$BASE/v1/proofing-sessions/$1/result")
    echo "$R" | grep -q '"status":"COMPLETED"' && { echo "$R"; return 0; }
    sleep 1
  done
  fail "proofing session $1 did not complete: $R"
}
SID=$(new_session)
[ -n "$SID" ] || fail "proofing session was not created"
curl -s -X POST "$BASE/v1/proofing-sessions/$SID/biometrics" -H "Content-Type: application/json" \
  --data "{\"type\":\"SELFIE\",\"contentType\":\"image/jpeg\",\"content\":\"$(B64 'selfie bytes')\"}" | grep -q '"sha256"' || fail "selfie rejected"
curl -s -X POST "$BASE/v1/proofing-sessions/$SID/documents" -H "Content-Type: application/json" \
  --data "{\"type\":\"DOCUMENT_FRONT\",\"contentType\":\"image/jpeg\",\"content\":\"$(B64 'document bytes')\"}" | grep -q '"sha256"' || fail "document rejected"
RES=$(wait_session "$SID")
echo "$RES" | grep -q '"result":"APPROVED"' || fail "expected APPROVED: $RES"
curl -s "$BASE/v1/proofing-sessions/$SID" | grep -q '12345678909' && fail "CPF is not masked"
DEL=$(curl -s -o /dev/null -w '%{http_code}' -X DELETE "$BASE/v1/proofing-sessions/$SID/evidence")
[ "$DEL" = "204" ] || fail "evidence deletion expected 204, got $DEL"
curl -s "$BASE/v1/proofing-sessions/$SID/result" | grep -q '"result":"APPROVED"' || fail "result lost after evidence deletion"

SID2=$(new_session)
curl -s -X POST "$BASE/v1/proofing-sessions/$SID2/biometrics" -H "Content-Type: application/json" \
  --data "{\"type\":\"SELFIE\",\"contentType\":\"image/jpeg\",\"content\":\"$(B64 'selfie FAKE_SPOOF')\"}" >/dev/null
curl -s -X POST "$BASE/v1/proofing-sessions/$SID2/documents" -H "Content-Type: application/json" \
  --data "{\"type\":\"DOCUMENT_FRONT\",\"contentType\":\"image/jpeg\",\"content\":\"$(B64 'document bytes')\"}" >/dev/null
wait_session "$SID2" | grep -q '"result":"REJECTED"' || fail "spoofed selfie should be REJECTED"

echo "== reconciliation: manual reconcile and history"
RECP=$(curl -s -X POST "$BASE/v1/signature-processes" -H "Idempotency-Key: smoke-rc-$RANDOM-$(date +%s)" -H "Content-Type: application/json" --data @"$SAMPLE" | field processId)
RR=$(curl -s -X POST "$BASE/v1/signature-processes/$RECP/reconcile")
echo "$RR" | grep -qE '"outcome":"(NOT_APPLICABLE|CONSISTENT|CORRECTED)"' || fail "unexpected reconcile outcome: $RR"
for i in $(seq 1 60); do
  curl -s "$BASE/v1/signature-processes/$RECP/status" | grep -q '"businessStatus":"COMPLETED"' && break
  sleep 1
  [ "$i" = 60 ] && fail "reconciled process did not complete"
done
curl -s "$BASE/v1/signature-processes/$RECP/reconciliations" | grep -q '"trigger":"MANUAL"' || fail "manual reconciliation missing from history"
RT=$(curl -s -o /dev/null -w '%{http_code}' -X POST "$BASE/v1/signature-processes/$RECP/reconcile")
[ "$RT" = "409" ] || fail "reconcile of a completed process expected 409, got $RT"
RN=$(curl -s -o /dev/null -w '%{http_code}' -X POST "$BASE/v1/signature-processes/sig_nope/reconcile")
[ "$RN" = "404" ] || fail "reconcile of unknown process expected 404, got $RN"

echo "== cancel a new process"
ID2=$(curl -s -X POST "$BASE/v1/signature-processes" -H "Idempotency-Key: smoke-c-$RANDOM-$(date +%s)" \
  -H "Content-Type: application/json" --data @"$SAMPLE" | field processId)
C1=$(curl -s -o /dev/null -w '%{http_code}' -X POST "$BASE/v1/signature-processes/$ID2/cancel")
[ "$C1" = "200" ] || fail "cancel expected 200, got $C1"
C2=$(curl -s -o /dev/null -w '%{http_code}' -X POST "$BASE/v1/signature-processes/$ID/cancel")
[ "$C2" = "409" ] || fail "cancel of completed expected 409, got $C2"


echo "== operations portal (spec 007)"
PORTAL="${PORTAL_URL:-http://localhost:3000}"
curl -s "$PORTAL/" | grep -q '<div id="root">' || fail "portal index not served"
SPA=$(curl -s -o /dev/null -w '%{http_code}' "$PORTAL/processes/$ID")
[ "$SPA" = "200" ] || fail "SPA fallback expected 200, got $SPA"
curl -s "$PORTAL/v1/signature-processes?q=$ID" | grep -q "\"$ID\"" || fail "process list via portal proxy missing process"
curl -s "$BASE/v1/signature-processes?status=COMPLETED&pageSize=5" | grep -q '"items"' || fail "list with filters failed"
curl -s "$BASE/v1/signature-processes/$ID" | grep -q '"sla"' || fail "detail without sla"
command curl -s "$BASE/v1/signature-processes/$ID/provider" -H "Authorization: Bearer $TOKEN_OPERATOR" | grep -q "\"provider\"" || fail "provider info failed"
curl -s "$BASE/v1/signature-processes/$ID/events?pageSize=200" | grep -q "\"id\":\"operator\"" || fail "operator identity missing from audit trail"
BADOP=$(curl -s -o /dev/null -w '%{http_code}' "$BASE/v1/signature-processes" -H "X-Operator-Id: bad id!")
[ "$BADOP" = "200" ] || fail "X-Operator-Id must be ignored when authenticated (expected 200), got $BADOP"

echo "== security: authentication, roles and client segregation (spec 008)"
code() { command curl -s -o /dev/null -w '%{http_code}' "$@"; }
[ "$(code "$BASE/v1/signature-processes")" = "401" ] || fail "request without token expected 401"
[ "$(code "$BASE/v1/signature-processes" -H "Authorization: Bearer not-a-token")" = "401" ] || fail "invalid token expected 401"
[ "$(code "$BASE/health")" = "200" ] || fail "health must stay public"
IDA=$(command curl -s -X POST "$BASE/v1/signature-processes" -H "Authorization: Bearer $TOKEN_A" -H "Idempotency-Key: smoke-a-$RANDOM-$(date +%s)" \
  -H "Content-Type: application/json" --data @"$SAMPLE" | field processId)
[ -n "$IDA" ] || fail "client A could not create a process"
[ "$(code "$BASE/v1/signature-processes/$IDA" -H "Authorization: Bearer $TOKEN_A")" = "200" ] || fail "client A must read its own process"
[ "$(code "$BASE/v1/signature-processes/$IDA" -H "Authorization: Bearer $TOKEN_B")" = "404" ] || fail "client B must not see client A process (404)"
[ "$(code -X POST "$BASE/v1/signature-processes/$IDA/cancel" -H "Authorization: Bearer $TOKEN_B")" = "404" ] || fail "client B must not cancel client A process"
command curl -s "$BASE/v1/signature-processes?pageSize=200" -H "Authorization: Bearer $TOKEN_B" | grep -q "$IDA" && fail "client B list leaked client A process"
command curl -s "$BASE/v1/signature-processes?pageSize=200" -H "Authorization: Bearer $TOKEN_OPERATOR" | grep -q "$IDA" || fail "operator must see all clients"
[ "$(code -X POST "$BASE/v1/signature-processes/$IDA/cancel" -H "Authorization: Bearer $TOKEN_VIEWER")" = "403" ] || fail "viewer cancel expected 403"
[ "$(code "$BASE/v1/signature-processes/$IDA" -H "Authorization: Bearer $TOKEN_VIEWER")" = "200" ] || fail "viewer must read"
[ "$(code -X POST "$BASE/v1/signature-processes" -H "Authorization: Bearer $TOKEN_OPERATOR" -H "Idempotency-Key: smoke-op-$RANDOM" -H "Content-Type: application/json" --data @"$SAMPLE")" = "403" ] || fail "operator create expected 403"
[ "$(code -X POST "$BASE/v1/signature-processes/$IDA/cancel" -H "Authorization: Bearer $TOKEN_OPERATOR")" = "200" ] || fail "operator cancel expected 200"
command curl -s "$BASE/v1/signature-processes/$IDA/events?pageSize=200" -H "Authorization: Bearer $TOKEN_OPERATOR" | grep -q '"id":"operator"' || fail "cancel not audited with the operator identity"
PROXY="${PORTAL_URL:-http://localhost:3000}/realms/orchestrator/protocol/openid-connect/token"
[ "$(code -X POST "$PROXY" -d grant_type=password -d client_id=portal -d username=viewer -d password=viewer)" != "200" ] || fail "portal client must not allow password grant"
[ "$(code -X POST "$PROXY" -d grant_type=authorization_code -d client_id=portal -d code=bogus -d code_verifier=bogus -d redirect_uri=http://localhost:3000/auth/callback)" = "400" ] || fail "token endpoint via portal proxy expected 400 for a bogus code"
AUTH="${KEYCLOAK_URL:-http://localhost:8180}/realms/orchestrator/protocol/openid-connect/auth?client_id=portal&response_type=code&scope=openid&redirect_uri=http://localhost:3000/auth/callback&state=s"
[ "$(code "$AUTH&code_challenge=E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM&code_challenge_method=S256")" = "200" ] || fail "authorization code request with PKCE expected 200"
[ "$(code "$AUTH")" != "200" ] || fail "authorization request without PKCE must be refused"

echo "== security hardening: proofing sessions and callbacks per client, Postgres on 5433 (spec 009)"
PS=$(command curl -s -X POST "$BASE/v1/proofing-sessions" -H "Authorization: Bearer $TOKEN_A" -H "Idempotency-Key: smoke-ps-$RANDOM-$(date +%s)" -H "Content-Type: application/json" \
  --data '{"externalId":"SMOKE-KYC","subject":{"name":"Maria Souza","document":"12345678909"},"validations":[{"type":"PERSON_DATA","required":true}]}' | field sessionId)
[ -n "$PS" ] || fail "client A could not create a proofing session"
[ "$(code "$BASE/v1/proofing-sessions/$PS" -H "Authorization: Bearer $TOKEN_A")" = "200" ] || fail "owner must read its proofing session"
[ "$(code "$BASE/v1/proofing-sessions/$PS" -H "Authorization: Bearer $TOKEN_B")" = "404" ] || fail "client B must not read client A proofing session"
[ "$(code "$BASE/v1/proofing-sessions/$PS/result" -H "Authorization: Bearer $TOKEN_OPERATOR")" = "200" ] || fail "operator reads any proofing session"
CBX="smoke-cb-$RANDOM-$(date +%s)"
command curl -s -o /dev/null -X POST "$BASE/v1/callbacks" -H "Authorization: Bearer $TOKEN_A" -H "Content-Type: application/json" --data "{\"callbackId\":\"$CBX\",\"url\":\"http://callback-sink:8080/hook\"}"
[ "$(code "$BASE/v1/callbacks/$CBX" -H "Authorization: Bearer $TOKEN_A")" = "200" ] || fail "owner must read its callback"
[ "$(code "$BASE/v1/callbacks/$CBX" -H "Authorization: Bearer $TOKEN_B")" = "404" ] || fail "client B must not read client A callback"
[ "$(code -X DELETE "$BASE/v1/callbacks/$CBX" -H "Authorization: Bearer $TOKEN_B")" = "404" ] || fail "client B must not deactivate client A callback"
(exec 3<>/dev/tcp/localhost/5433) 2>/dev/null || fail "Postgres is not reachable on host port 5433"

echo "== signers, confirmation and progress (spec 010)"
SIGNERS_BAD='{"externalId":"SMOKE-BAD","document":{"fileName":"c.pdf","source":{"type":"URL","url":"http://sample-docs/contrato.pdf"}},"defaults":{"signatureType":"ADVANCED","confirmation":["SMS"]},"signers":[{"name":"Maria","document":"12345678909"}]}'
BAD=$(curl -s -w '\n%{http_code}' -X POST "$BASE/v1/signature-processes" -H "Idempotency-Key: smoke-bad-$RANDOM-$(date +%s)" -H "Content-Type: application/json" --data "$SIGNERS_BAD")
[ "$(tail -1 <<<"$BAD")" = "400" ] || fail "SMS without phone expected 400"
grep -q 'signers\[0\].phone' <<<"$BAD" || fail "validation error must point at signers[0].phone"
printf '%%PDF-1.4\nsmoke upload document\n' > .smoke-upload.pdf  # relative path: native curl on Windows cannot read /tmp
UPID=$(curl -s -X POST "$BASE/v1/document-uploads" -F "file=@.smoke-upload.pdf;type=application/pdf" | field uploadId)
rm -f .smoke-upload.pdf
[ -n "$UPID" ] || fail "document upload failed"
SEQ='{"externalId":"SMOKE-SEQ-'$RANDOM'","document":{"fileName":"c.pdf","source":{"type":"UPLOAD","uploadId":"'$UPID'"}},"defaults":{"signatureType":"ADVANCED","confirmation":["EMAIL"]},"signers":[{"name":"Maria Souza","document":"12345678909","email":"maria@example.com","order":1},{"name":"Joao Lima","document":"12345678909","email":"joao@example.com","order":2}]}'
SQ=$(curl -s -X POST "$BASE/v1/signature-processes" -H "Idempotency-Key: smoke-seq-$RANDOM-$(date +%s)" -H "Content-Type: application/json" --data "$SEQ" | field processId)
[ -n "$SQ" ] || fail "sequential process not created"
wait_codes() { # $1 = number of codes expected
  for i in $(seq 1 60); do
    CODES=$(curl -s "$BASE/v1/dev/confirmation-codes/$SQ")
    [ "$(grep -o '"code":"[0-9]*"' <<<"$CODES" | wc -l)" -ge "$1" ] && return 0
    sleep 1
  done
  fail "expected $1 confirmation code(s) in the simulated sink"
}
wait_codes 1
SG=($(curl -s "$BASE/v1/signature-processes/$SQ" | grep -o '"id":"sgn_[^"]*"' | sed 's/"id":"\(.*\)"/\1/'))
C1=$(grep -o '"code":"[0-9]*"' <<<"$CODES" | sed -n 1p | sed 's/[^0-9]//g')
sleep 3
[ "$(grep -o '"code":"[0-9]*"' <<<"$(curl -s "$BASE/v1/dev/confirmation-codes/$SQ")" | wc -l)" = "1" ] || fail "the second signer must not receive a code before the first signs"
curl -s "$BASE/v1/signature-processes/$SQ" | grep -q '"signed":true' && fail "nobody may sign before confirming"
WRONG=$([ "$C1" = "000000" ] && echo 111111 || echo 000000)
[ "$(code -X POST "$BASE/v1/signature-processes/$SQ/signers/${SG[0]}/confirmations/EMAIL/confirm" -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" --data "{\"code\":\"$WRONG\"}")" = "422" ] || fail "wrong code expected 422"
[ "$(code -X POST "$BASE/v1/signature-processes/$SQ/signers/${SG[0]}/confirmations/EMAIL/confirm" -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" --data "{\"code\":\"$C1\"}")" = "200" ] || fail "right code expected 200"
wait_codes 2
C2=$(grep -o '"code":"[0-9]*"' <<<"$CODES" | sed -n 2p | sed 's/[^0-9]//g')
[ "$(code -X POST "$BASE/v1/signature-processes/$SQ/signers/${SG[1]}/confirmations/EMAIL/confirm" -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" --data "{\"code\":\"$C2\"}")" = "200" ] || fail "second signer code expected 200"
for i in $(seq 1 60); do
  [ "$(curl -s "$BASE/v1/signature-processes/$SQ/status" | field businessStatus)" = "COMPLETED" ] && break
  sleep 1
  [ "$i" = 60 ] && fail "sequential process did not complete"
done
DET=$(curl -s "$BASE/v1/signature-processes/$SQ")
grep -q '"completedSteps":7,"totalSteps":7' <<<"$DET" || grep -q '"completedSteps":6,"totalSteps":6' <<<"$DET" || fail "completed process must have completedSteps = totalSteps: $(grep -o '"progress":{[^[]*' <<<"$DET")"
curl -s "$BASE/v1/signature-processes?pageSize=200" | grep -q '"progress":{"completedSteps"' || fail "list must carry progress"
EVS=$(curl -s "$BASE/v1/signature-processes/$SQ/events?pageSize=200")
for t in CONFIRMATION_SENT CONFIRMATION_CONFIRMED SIGNER_RELEASED CONFIRMATION_CODE_REJECTED; do grep -q "\"$t\"" <<<"$EVS" || fail "event $t missing"; done
for c in "$C1" "$C2"; do grep -q "$c" <<<"$EVS" && fail "a confirmation code leaked into the journal"; done
curl -s "$BASE/swagger/v1/swagger.json" | grep -q 'confirmations/{channel}/confirm' || fail "swagger must document the confirmation endpoints"

echo "== real providers: simulated by default, webhooks signed and public (spec 011)"
curl -s "$BASE/v1/signature-processes/$ID" | grep -q '"provider":"SIMULATED"' || fail "the simulated provider must stay the default"
[ "$(code -X POST "$BASE/v1/webhooks/docusign" -H "Content-Type: application/json" --data '{"event":"envelope-completed"}')" = "401" ] || fail "DocuSign webhook without signature expected 401 (and no token needed)"
[ "$(code -X POST "$BASE/v1/webhooks/docusign" -H "X-DocuSign-Signature-1: AAAA" -H "Content-Type: application/json" --data '{}')" = "401" ] || fail "DocuSign webhook with a bad signature expected 401"
[ "$(code -X POST "$BASE/v1/webhooks/lacuna" -H "X-Webhook-Secret: wrong" -H "Content-Type: application/json" --data '{}')" = "401" ] || fail "Lacuna webhook with a bad secret expected 401"
[ "$(code -X POST "$BASE/v1/webhooks/unknown" -H "Content-Type: application/json" --data '{}')" = "404" ] || fail "unknown provider webhook expected 404"
curl -s "$BASE/swagger/v1/swagger.json" | grep -q '/v1/webhooks' && fail "webhook routes must not be in the public contract"
git check-ignore -q .env 2>/dev/null || fail ".env must be git-ignored"
[ -f .env.example ] || fail ".env.example is missing"

echo "== observability: metrics reach the collector (spec 008)"
METRICS="${METRICS_URL:-http://localhost:8889/metrics}"
need="orchestrator_process_created orchestrator_process_completed orchestrator_provider_latency orchestrator_callback_deliveries orchestrator_deadletter_size orchestrator_security_denied"
for i in $(seq 1 40); do
  body=$(command curl -s "$METRICS" || true)
  missing=""
  for m in $need; do grep -q "$m" <<<"$body" || missing="$missing $m"; done
  [ -z "$missing" ] && break
  sleep 3
  [ "$i" = 40 ] && fail "metrics missing in collector:$missing"
done

echo "SMOKE TEST PASSED"
