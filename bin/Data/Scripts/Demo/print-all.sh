#!/bin/sh

printf 'Demo / print-all\n'
printf 'TEXT=%s\n' "$TEXT"
printf 'COUNT=%s\n' "$COUNT"
printf 'ENABLED=%s\n' "$ENABLED"
printf 'MODE=%s\n' "$MODE"

# Demo only: printing a secret exposes it in the execution log.
printf 'TOKEN=%s\n' "$TOKEN"
