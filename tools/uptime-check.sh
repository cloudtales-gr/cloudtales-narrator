#!/usr/bin/env bash
# Availability check for the blog and the narrator Function App. Exit code 1 = something is down.
# Each check is retried 3 times, 20 s apart, so a single slow response does not raise an alarm.
# Usage: SITE=https://cloudtales.gr FUNC=https://<app>.azurewebsites.net tools/uptime-check.sh
set -uo pipefail

SITE="${SITE:-https://cloudtales.gr}"
FUNC="${FUNC:?set FUNC to the Function App base URL}"
CERT_MIN_DAYS="${CERT_MIN_DAYS:-14}"
MAX_SECONDS="${MAX_SECONDS:-15}"
RETRY_DELAY="${RETRY_DELAY:-20}"
failed=0
summary="${GITHUB_STEP_SUMMARY:-/dev/null}"
echo "| Check | Result | Time |" >> "$summary"
echo "|---|---|---|" >> "$summary"

# check <name> <url> <expected status, regex> [text the body must contain] [extra curl args...]
check() {
  local name="$1" url="$2" want="$3" text="${4:-}"; shift 4 || shift $#
  local attempt code time body
  body="$(mktemp)"
  for attempt in 1 2 3; do
    read -r code time < <(curl -sS -o "$body" -w '%{http_code} %{time_total}' --max-time "$MAX_SECONDS" \
      -A "CloudTales-Uptime/1.0 (+https://github.com/cloudtales-gr/cloudtales-narrator)" "$@" "$url" 2>/dev/null || echo "000 0")
    if [[ "$code" =~ ^($want)$ ]] && { [[ -z "$text" ]] || grep -qF -- "$text" "$body"; }; then
      echo "OK    $name ($code, ${time}s)"
      echo "| $name | ✅ $code | ${time}s |" >> "$summary"
      rm -f "$body"; return 0
    fi
    [[ $attempt -lt 3 ]] && sleep "$RETRY_DELAY"
  done
  echo "::error title=DOWN: $name::$url returned $code (expected $want${text:+ with \"$text\"})"
  echo "| $name | ❌ $code | ${time}s |" >> "$summary"
  failed=1; rm -f "$body"
}

check "Home page"          "$SITE/"                                  200 "CloudTales"
check "Article"            "$SITE/zero-key-architecture-azure/"      200 "Zero-Key"
check "Search index"       "$SITE/pagefind/pagefind.js"              200 ""
check "RSS feed"           "$SITE/index.xml"                         200 "<rss"
check "Narration feed"     "$SITE/narration.json"                    200 "\"posts\""
check "www redirect"       "https://www.${SITE#https://}/"           "301|302|307|308" ""
check "Podcast feed"       "$SITE/podcast.xml"                       200 "<itunes:" -L
check "Narrator: audio"    "$FUNC/api/audio/zero-key-architecture-azure" 302 "" -H "Referer: $SITE/"
check "Narrator: contact"  "$FUNC/api/contact"                       403 "" -X POST -H "Origin: https://example.com" -d '{}'

# TLS certificate of the site: days until it expires (Static Web Apps renews it; this catches a stuck renewal)
host="${SITE#https://}"
expiry="$(echo | openssl s_client -servername "$host" -connect "$host:443" 2>/dev/null | openssl x509 -noout -enddate 2>/dev/null | cut -d= -f2)"
if [[ -n "$expiry" ]]; then
  days=$(( ( $(date -d "$expiry" +%s) - $(date +%s) ) / 86400 ))
  if (( days < CERT_MIN_DAYS )); then
    echo "::error title=TLS certificate::$host expires in $days days ($expiry)"
    echo "| TLS certificate | ❌ $days days left | |" >> "$summary"; failed=1
  else
    echo "OK    TLS certificate ($days days left)"
    echo "| TLS certificate | ✅ $days days left | |" >> "$summary"
  fi
else
  echo "::error title=TLS certificate::could not read the certificate of $host"
  failed=1
fi

exit $failed
