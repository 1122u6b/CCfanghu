#!/bin/bash
# Customer flow on a real Mac, after the package was installed. Needs TEST_CODE, SOCKS_PORT, SOCKS_PASS.
set -uo pipefail
cd "$(dirname "$0")"
FAIL=(); fail(){ echo "FAIL: $1"; FAIL+=("$1"); }
ask(){ python3 ask.py "$1"; }
state(){ python3 ask.py '{"type":"status"}' 2>/dev/null | python3 -c 'import json,sys;d=json.loads(sys.stdin.read() or "{}");p=d.get("value",{}).get("protection",{});print(p.get("state",""),p.get("reason",""))' 2>/dev/null; }
wait_for(){ local end=$((SECONDS+$2)); while [ $SECONDS -lt $end ]; do s=$(state); [[ "$s" =~ $1 ]] && { echo "$s"; return 0; }; sleep 2; done; echo "$(state)"; return 1; }
ip(){ for k in 1 2 3; do r=$(curl -s --max-time 20 https://api.ipify.org) && [[ "$r" =~ ^[0-9.]+$ ]] && { echo "$r"; return; }; sleep 3; done; echo "error"; }
PHYS=$(cat /tmp/leyra-physical-if); REAL=$(cat /tmp/leyra-real-ip)

echo "1. service running and answering only its owner"
sudo launchctl print system/xin.leyra.agent >/dev/null 2>&1 || fail "service not loaded in launchd"
ls -l /var/run/leyra/agent.sock || fail "socket missing"
ask '{"type":"status"}' || fail "status request failed"

echo "2. activation"
ask '{"type":"activate","code":"ZZZZ-ZZZZ-ZZZZ-ZZZZ","contact":"ci"}' && fail "wrong code accepted"
ask '{"type":"consent","version":99}' >/dev/null || true
ask "{\"type\":\"activate\",\"code\":\"$TEST_CODE\",\"contact\":\"automated mac test\"}" || fail "activation failed"
s=$(wait_for 'provisioning' 60) || fail "after activation it should wait for the own exit: $s"

echo "3. own exit -> protected"
ask "{\"type\":\"own_exit\",\"links\":\"socks5://ci:$SOCKS_PASS@127.0.0.1:$SOCKS_PORT\"}" || fail "importing the own exit failed"
s=$(wait_for '^protected' 150) || fail "not protected after connecting the own exit: $s"; echo "   state: $s"

echo "4. sites see the exit, not this Mac"
EXIT=$(ip); echo "   real $REAL, now seen as $EXIT"
[[ "$EXIT" =~ ^[0-9.]+$ && "$EXIT" != "$REAL" ]] || fail "traffic does not go through the exit: $EXIT"

echo "5. firewall rules: a program going around the tunnel is blocked"
sudo pfctl -a com.apple/250.LeyraConnect -s rules | grep -q leyra-default-block || fail "pf rules not loaded"
if curl -s --max-time 8 --interface "$PHYS" https://1.1.1.1 -o /dev/null; then fail "direct connection on $PHYS was not blocked"; else echo "   direct connection on $PHYS blocked"; fi

echo "6. only listed sites through the exit"
ask '{"type":"set_route_mode","listed":true,"domains":["ipify.org"]}' >/dev/null; sleep 4; wait_for '^protected' 150 >/dev/null || fail "list mode not protected"
L=$(ip); ask '{"type":"set_route_mode","listed":true,"domains":["example.com"]}' >/dev/null; sleep 4; wait_for '^protected' 150 >/dev/null
D=$(ip); W=$(curl -s -o /dev/null -w '%{http_code}' --max-time 20 https://www.apple.com/)
ask '{"type":"set_route_mode","listed":false}' >/dev/null; sleep 4; wait_for '^protected' 150 >/dev/null
B=$(ip); echo "   listed: $L, not listed: $D (other site $W), back: $B"
[ "$L" = "$EXIT" ] || fail "listed site did not go through the exit: $L"
[[ "$D" =~ ^[0-9.]+$ && "$D" != "$EXIT" ]] || fail "unlisted site did not connect directly: $D"
[ "$W" = "200" ] || [ "$W" = "301" ] || fail "direct connections blocked in list mode: $W"
[ "$B" = "$EXIT" ] || fail "back to everything through the exit failed: $B"

echo "7. pause: rules removed, normal network back"
ask '{"type":"pause","minutes":10}' >/dev/null || ask '{"type":"pause","reason":"quit"}' >/dev/null
s=$(wait_for '^paused' 30) || fail "did not pause: $s"
sudo pfctl -a com.apple/250.LeyraConnect -s rules | grep -q leyra-default-block && fail "pf rules still loaded after pause"
P=$(ip); [ "$P" = "$REAL" ] || fail "network not back to normal after pause: $P"

echo "--- service log ---"; sudo cat "/Library/Application Support/LeyraConnect/agent-error.log" 2>/dev/null | tail -20
if [ ${#FAIL[@]} -gt 0 ]; then echo "LEYRA_MAC_E2E=FAIL (${#FAIL[@]})"; exit 1; fi
echo "LEYRA_MAC_E2E=PASS"
