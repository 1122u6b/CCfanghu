#!/bin/bash
# Mac coexist mode: the customer's own proxy app stays on; Claude (the list) goes through the Leyra exit.
# Runs after e2e.sh on the same Mac (already activated, own exit connected, protection paused at the end).
set -uo pipefail
cd "$(dirname "$0")"
FAIL=(); fail(){ echo "FAIL: $1"; FAIL+=("$1"); }
ask(){ python3 ask.py "$1"; }
state(){ python3 ask.py '{"type":"status"}' --full 2>/dev/null | python3 -c 'import json,sys;d=json.loads(sys.stdin.read() or "{}");p=d.get("value",{}).get("protection",{});print(p.get("state",""),p.get("reason",""))' 2>/dev/null; }
wait_for(){ local end=$((SECONDS+$2)); while [ $SECONDS -lt $end ]; do s=$(state); [[ "$s" =~ $1 ]] && { echo "$s"; return 0; }; sleep 2; done; echo "$(state)"; return 1; }
SVC=$(networksetup -listallnetworkservices | tail -n +2 | grep -v '^\*' )
proxyof(){ networksetup -getwebproxy "$(echo "$SVC" | head -1)" | tr '\n' ' '; }
REAL=$(cat /tmp/leyra-real-ip); LOG=/tmp/own-proxy.log; : > $LOG

echo "1. the customer's own proxy app is running and is the system proxy"
python3 local_proxy.py 7890 $LOG & OWN=$!; sleep 1
echo "$SVC" | while read -r s; do sudo networksetup -setwebproxy "$s" 127.0.0.1 7890; sudo networksetup -setsecurewebproxy "$s" 127.0.0.1 7890; done
echo "   before Leyra: $(proxyof)"

echo "2. Leyra on, with ipify.org on the list (stands in for Claude)"
ask '{"type":"set_route_mode","listed":false,"domains":["ipify.org"]}' >/dev/null
ask '{"type":"resume"}' >/dev/null
s=$(wait_for '^protected' 150) || fail "not protected in coexist mode: $s"; echo "   state: $s"
echo "   system proxy now: $(proxyof)"
proxyof | grep -q "Port: 17890" || fail "system proxy was not put in front of the customer's proxy"
sudo pfctl -a com.apple/250.LeyraConnect -s rules 2>/dev/null | grep -q leyra-default-block && fail "firewall rules loaded in coexist mode (would cut the customer's proxy)"

echo "3. the listed site goes through the Leyra exit, others through the customer's own proxy"
L=$(curl -s --max-time 25 -x http://127.0.0.1:17890 https://api.ipify.org)
O=$(curl -s --max-time 25 -x http://127.0.0.1:17890 https://icanhazip.com | tr -d '\n')
echo "   listed: $L, other site: $O (real $REAL); own proxy handled: $(sort -u $LOG | tr '\n' ' ')"
[[ "$L" =~ ^[0-9.]+$ && "$L" != "$REAL" ]] || fail "listed site did not go through the exit: $L"
grep -q icanhazip $LOG || fail "other sites were not handed to the customer's own proxy"
grep -q ipify $LOG && fail "the listed site went through the customer's proxy instead of the exit"

echo "4. the customer's proxy app sets itself back as system proxy: Leyra takes it again"
echo "$SVC" | while read -r s; do sudo networksetup -setwebproxy "$s" 127.0.0.1 7890; done
end=$((SECONDS+20)); until proxyof | grep -q "Port: 17890" || [ $SECONDS -gt $end ]; do sleep 2; done
proxyof | grep -q "Port: 17890" && echo "   taken again" || fail "Leyra did not take the system proxy back"

echo "5. pause: the customer's own system proxy is back exactly"
ask '{"type":"pause","minutes":10}' >/dev/null; wait_for '^paused' 30 >/dev/null || fail "did not pause"
echo "   after pause: $(proxyof)"
proxyof | grep -q "Port: 7890" || fail "the customer's system proxy was not restored"
kill $OWN 2>/dev/null
if [ ${#FAIL[@]} -gt 0 ]; then echo "LEYRA_MAC_COEXIST=FAIL (${#FAIL[@]})"; exit 1; fi
echo "LEYRA_MAC_COEXIST=PASS"
