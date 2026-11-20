#!/bin/bash
# Customer flow on a real Mac, after the package was installed. Needs TEST_CODE, SOCKS_PORT, SOCKS_PASS.
set -uo pipefail
cd "$(dirname "$0")"
FAIL=(); fail(){ echo "FAIL: $1"; FAIL+=("$1"); }
ask(){ python3 ask.py "$1"; }
state(){ python3 ask.py '{"type":"status"}' --full 2>/dev/null | python3 -c 'import json,sys;d=json.loads(sys.stdin.read() or "{}");p=d.get("value",{}).get("protection",{});print(p.get("state",""),p.get("reason",""))' 2>/dev/null; }
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
FRESH=$(python3 ask.py '{"type":"status"}' --full | python3 -c 'import json,sys;v=json.load(sys.stdin)["value"];print(v.get("listed"),v.get("mode_offer"))')
echo "   fresh install: only Claude through the exit, asks which way = $FRESH"; [ "$FRESH" = "True False" ] || fail "a new install should send only Claude through the exit and not ask: $FRESH"

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
# icanhazip.com stands in for a listed site (ipify.org always goes through the exit: the browser check uses it).
ip2(){ for k in 1 2 3; do r=$(curl -s --max-time 20 https://icanhazip.com | tr -d '\n') && [[ "$r" =~ ^[0-9.]+$ ]] && { echo "$r"; return; }; sleep 3; done; echo "error"; }
ask '{"type":"set_route_mode","listed":true,"domains":["icanhazip.com"]}' >/dev/null; sleep 4; wait_for '^protected' 150 >/dev/null || fail "list mode not protected"
L=$(ip2); ask '{"type":"set_route_mode","listed":true,"domains":["example.com"]}' >/dev/null; sleep 4; wait_for '^protected' 150 >/dev/null
D=$(ip2); W=$(curl -s -o /dev/null -w '%{http_code}' --max-time 20 https://www.apple.com/)
echo "   only Claude through the exit and the exit dies: only Claude (and the list) is cut off, everything else keeps working"
curl -s --max-time 8 -x http://127.0.0.1:17890 http://stop-60.leyra-ci.invalid/ >/dev/null || true
s=$(wait_for '^blocked' 100); echo "   exit dead in list mode: $s"
[[ "$s" == blocked* ]] || fail "list mode: a dead exit should cut Claude off: $s"
OTHER=$(curl -s -o /dev/null -w '%{http_code}' --max-time 15 https://icanhazip.com || true)
LISTED=$(curl -s -o /dev/null -w '%{http_code}' --max-time 8 https://example.com || true)
CLAUDE=$(curl -s -o /dev/null -w '%{http_code}' --max-time 8 https://claude.ai || true)
echo "   while the exit is down: other site $OTHER, listed site $LISTED, claude.ai $CLAUDE"
[ "$OTHER" = "200" ] || fail "list mode with the exit down: other sites must keep working, got $OTHER"
[ "$LISTED" = "000" ] || fail "list mode with the exit down: a listed site answered ($LISTED)"
[ "$CLAUDE" = "000" ] || fail "list mode with the exit down: claude.ai answered ($CLAUDE)"
s=$(wait_for '^protected' 150) || fail "list mode: not protected again once the exit came back: $s"; echo "   exit back: $s"
ask '{"type":"set_route_mode","listed":false}' >/dev/null; sleep 4; wait_for '^protected' 150 >/dev/null
B=$(ip); echo "   listed: $L, not listed: $D (other site $W), back: $B"
[ "$L" = "$EXIT" ] || fail "listed site did not go through the exit: $L"
[[ "$D" =~ ^[0-9.]+$ && "$D" != "$EXIT" ]] || fail "unlisted site did not connect directly: $D"
[ "$W" = "200" ] || [ "$W" = "301" ] || fail "direct connections blocked in list mode: $W"
[ "$B" = "$EXIT" ] || fail "back to everything through the exit failed: $B"
# Traffic through the exit is counted by the server (saved once a minute) and the exit shows its own address.
acct(){ python3 ask.py '{"type":"account"}' --full 2>/dev/null | python3 -c 'import json,sys;v=json.loads(sys.stdin.read() or "{}").get("value",{});print(v.get("traffic_used_bytes"),v.get("traffic_reset_at"),",".join((x.get("exit_ip") or "-") for x in v.get("exits",{}).get("list",[])))' 2>/dev/null; }
for i in $(seq 1 20); do read -r USED RESET EIPS <<<"$(acct)"; [[ "$USED" =~ ^[0-9]+$ && "$USED" -gt 0 ]] && break; sleep 6; done
echo "   traffic counted by the server: $USED bytes, resets at $RESET, exit addresses: $EIPS"
[[ "$USED" =~ ^[0-9]+$ && "$USED" -gt 0 ]] || fail "server did not count the traffic through the exit: $USED"
[[ "$RESET" =~ ^[0-9]+$ ]] || fail "traffic reset date missing: $RESET"
[[ ",$EIPS," == *",$EXIT,"* ]] || fail "the exit does not show its own address: $EIPS (expected $EXIT)"
echo "   switching between working exits is quick and protection never drops (no rebuild)"
ask "{\"type\":\"own_exit\",\"links\":\"socks5://ci:$SOCKS_PASS@127.0.0.1:$SOCKS_PORT\",\"name\":\"second\"}" >/dev/null || fail "could not add a second working exit"
s=$(state); [[ "$s" == protected* ]] || fail "adding a working exit dropped protection: $s"
other(){ python3 ask.py '{"type":"account"}' --full | python3 -c 'import json,sys;l=json.load(sys.stdin)["value"]["exits"]["list"];print(([x["id"] for x in l if not x["current"]] or [""])[0])'; }
FIRST=$(other); T0=$(date +%s)
ask "{\"type\":\"select_exit\",\"id\":$FIRST}" >/dev/null || fail "switching back to the first exit failed"
T1=$(date +%s); s=$(state); echo "   switched back in $((T1-T0)) s, state: $s"
[[ "$s" == protected* ]] || fail "switching exits dropped protection: $s"
[ $((T1-T0)) -le 25 ] || fail "switching exits took $((T1-T0)) s"
P=$(ip); [ "$P" = "$EXIT" ] || fail "after switching, traffic does not go through the exit: $P"
SECOND=$(other); [ -n "$SECOND" ] && ask "{\"type\":\"delete_exit\",\"id\":$SECOND}" >/dev/null
s=$(state); [[ "$s" == protected* ]] || fail "deleting an exit not in use dropped protection: $s"
echo "   本机直连: this Mac dials the exit itself. The test exit only exists on the server, so here it cannot connect:"
echo "   the real address must stay hidden, and going back through Hong Kong must recover"
CUR=$(python3 ask.py '{"type":"account"}' --full | python3 -c 'import json,sys;l=json.load(sys.stdin)["value"]["exits"]["list"];print([x["id"] for x in l if x["current"]][0])')
ask "{\"type\":\"set_exit_direct\",\"id\":$CUR,\"direct\":true}" >/dev/null || fail "could not switch the exit to direct dialing"
sleep 15; s=$(state); echo "   dialing an unreachable exit directly: $s"
[[ "$s" == protected* ]] && fail "protected through an exit that cannot be reached: $s"
for i in 1 2 3; do P=$(curl -s --max-time 6 https://api.ipify.org || true); [ -n "$P" ] && fail "something got out while the only exit was unreachable: $P"; done
ask "{\"type\":\"set_exit_direct\",\"id\":$CUR,\"direct\":false}" >/dev/null || fail "could not switch back through Hong Kong"
s=$(wait_for '^protected' 150) || fail "not protected again after switching back through Hong Kong: $s"
P=$(ip); [ "$P" = "$EXIT" ] || fail "after switching back traffic does not go through the exit: $P"; echo "   back through Hong Kong: $s, address $P"
echo "   the exit in use stops passing traffic mid-session (like a dead node): Leyra notices and moves to the spare exit"
curid(){ python3 ask.py '{"type":"account"}' --full 2>/dev/null | python3 -c 'import json,sys;l=json.load(sys.stdin)["value"]["exits"]["list"];print(([x["id"] for x in l if x["current"]] or [""])[0])' 2>/dev/null; }
SPARE=$(python3 ask.py "{\"type\":\"own_exit\",\"links\":\"socks5://ci2:$SOCKS_PASS@127.0.0.1:$SOCKS_PORT\",\"name\":\"spare\"}" --full | python3 -c 'import json,sys;print(json.load(sys.stdin)["value"]["id"])')
ask "{\"type\":\"select_exit\",\"id\":$CUR}" >/dev/null || fail "could not go back to the first exit"
wait_for '^protected' 60 >/dev/null; sleep 5; BEFORE=$(ip)
curl -s --max-time 8 -x http://127.0.0.1:17890 http://stop-240.leyra-ci.invalid/ >/dev/null || true
T0=$(date +%s); MOVED=""
while [ $(( $(date +%s) - T0 )) -lt 90 ]; do
  P=$(curl -s --max-time 4 https://api.ipify.org || true)
  [ -n "$P" ] && [ "$P" != "$BEFORE" ] && fail "while the exit was dead, sites saw another address: $P"
  [ "$(curid)" = "$SPARE" ] && { MOVED=1; break; }; sleep 2
done
echo "   moved to the spare exit: ${MOVED:-no} after $(( $(date +%s) - T0 )) s"
TOOK=$(( $(date +%s) - T0 ))
[ -n "$MOVED" ] || fail "did not move away from an exit that stopped passing traffic within 90 s"
[ -n "$MOVED" ] && [ "$TOOK" -gt 60 ] && fail "moving away from an exit that stopped passing traffic took $TOOK s (more than 60 s)"
s=$(wait_for '^protected' 60) || fail "not protected on the spare exit: $s"
LF=$(python3 ask.py '{"type":"status"}' --full | python3 -c 'import json,sys;print((json.load(sys.stdin)["value"].get("last_failover") or {}).get("to",""))')
echo "   switch the customer is told about: -> $LF"; [ "$LF" = "spare" ] || fail "the automatic switch was not reported to the window: $LF"
echo "   automatic switching turned off: the dead exit cuts the network instead, nothing gets out, the exit is not changed"
ask '{"type":"set_auto_exit","on":false}' >/dev/null || fail "could not turn automatic switching off"
ask "{\"type\":\"select_exit\",\"id\":$CUR}" >/dev/null || fail "could not select the dead exit"
s=$(wait_for '^blocked' 90); echo "   with switching off: $s"
[[ "$s" == "blocked exit_down"* ]] || fail "with automatic switching off, a dead exit should cut off with reason exit_down: $s"
for i in 1 2 3; do P=$(curl -s --max-time 5 https://api.ipify.org || true); [ -n "$P" ] && fail "something got out while cut off: $P"; done
[ "$(curid)" = "$CUR" ] || fail "the exit was changed although automatic switching is off"
ask '{"type":"set_auto_exit","on":true}' >/dev/null || fail "could not turn automatic switching back on"
ask "{\"type\":\"select_exit\",\"id\":$SPARE}" >/dev/null || fail "could not select the spare exit"
s=$(wait_for '^protected' 150) || fail "not protected again on the spare exit: $s"; echo "   back on the spare exit: $s"
curl -s --max-time 8 -x http://127.0.0.1:17890 http://resume.leyra-ci.invalid/ >/dev/null || true   # the first test account passes traffic again for the later steps

echo "7. pause: rules removed, normal network back"
ask '{"type":"pause","minutes":10}' >/dev/null || ask '{"type":"pause","reason":"quit"}' >/dev/null
s=$(wait_for '^paused' 30) || fail "did not pause: $s"
sudo pfctl -a com.apple/250.LeyraConnect -s rules | grep -q leyra-default-block && fail "pf rules still loaded after pause"
P=$(ip); [ "$P" = "$REAL" ] || fail "network not back to normal after pause: $P"

echo "--- service log ---"; sudo cat "/Library/Application Support/LeyraConnect/agent-error.log" 2>/dev/null | tail -20
if [ ${#FAIL[@]} -gt 0 ]; then echo "LEYRA_MAC_E2E=FAIL (${#FAIL[@]})"; exit 1; fi
echo "LEYRA_MAC_E2E=PASS"
