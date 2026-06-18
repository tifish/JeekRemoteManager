#!/bin/sh

printf 'Demo / print-summary\n'
printf 'mode=%s enabled=%s count=%s\n' "$MODE" "$ENABLED" "$COUNT"
printf 'text length=%s\n' "$(printf '%s' "$TEXT" | wc -c | tr -d ' ')"
printf 'token length=%s\n' "$(printf '%s' "$TOKEN" | wc -c | tr -d ' ')"

exit 1
