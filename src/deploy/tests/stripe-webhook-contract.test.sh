#!/bin/sh
set -eu

deployment_dir=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
events=$deployment_dir/stripe-webhook-events.txt
runbook=$deployment_dir/../../docs/operations/hook2stream-mvp-runbook.md
fail() { printf '%s\n' "Stripe webhook contract test: $*" >&2; exit 1; }

[ -f "$events" ] || fail "canonical event list is missing"
expected=$(cat <<'EOF'
checkout.session.completed
checkout.session.async_payment_succeeded
checkout.session.async_payment_failed
checkout.session.expired
invoice.paid
invoice.payment_failed
invoice.finalization_failed
invoice.voided
invoice.marked_uncollectible
charge.refunded
charge.dispute.created
customer.subscription.deleted
customer.subscription.paused
customer.subscription.updated
EOF
)
[ "$(cat "$events")" = "$expected" ] || fail "canonical event list drifted"
[ "$(LC_ALL=C sort "$events" | uniq -d | wc -l)" -eq 0 ] || fail "canonical event list has duplicates"
! grep -Fxq 'charge.dispute.closed' "$events" || fail "unsafe automatic dispute restore event is canonical"
! grep -Fxq 'charge.dispute.funds_reinstated' "$events" || fail "unsafe automatic funds restore event is canonical"

for event in $expected; do
  grep -Fq "\`$event\`" "$runbook" || fail "runbook omits $event"
done

grep -Fq 'A won dispute is' "$runbook" || fail "manual won-dispute restore policy is missing"
grep -Fq 'webhook delivery never restores it' "$runbook" || fail "automatic dispute restore prohibition is missing"

printf '%s\n' "Stripe webhook contract test: paid, refund, failure, dispute, and subscription lifecycle events passed"
