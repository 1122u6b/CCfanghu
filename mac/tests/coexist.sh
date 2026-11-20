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

echo "1. Leyra already protecting normally (firewall rules on), then the customer starts their own proxy app"
ask '{"type":"resume"}' >/dev/null
s=$(wait_for '^protected' 150) || fail "normal protection did not come up: $s"
sudo pfctl -a com.apple/250.LeyraConnect -s rules 2>/dev/null | grep -q leyra-default-block || fail "normal protection had no firewall rules"
python3 local_proxy.py 7890 $LOG & OWN=$!; sleep 1
echo "$SVC" | while read -r s; do sudo networksetup -setwebproxy "$s" 127.0.0.1 7890; sudo networksetup -setsecurewebproxy "$s" 127.0.0.1 7890; done
echo "   own proxy set as system proxy: $(proxyof)"

echo "2. Leyra rebuilds into coexist mode, ipify.org on the list (stands in for a listed site)"
ask '{"type":"set_route_mode","listed":false,"domains":["ipify.org"]}' >/dev/null; sleep 4
s=$(wait_for '^protected' 150) || fail "not protected in coexist mode: $s"; echo "   state: $s"
OWNWEB=$(curl -s -o /dev/null -w '%{http_code}' --max-time 20 -x http://127.0.0.1:7890 https://www.apple.com/)
echo "   the customer's own proxy on its own: $OWNWEB"; [ "$OWNWEB" = "200" ] || [ "$OWNWEB" = "301" ] || fail "the customer's own proxy was blocked after switching to coexist mode: $OWNWEB"
echo "   system proxy now: $(proxyof)"
proxyof | grep -q "Port: 17890" || fail "system proxy was not put in front of the customer's proxy"
sudo pfctl -a com.apple/250.LeyraConnect -s rules 2>/dev/null | grep -q 'user >= 500' || fail "coexist mode has no firewall lock for the customer's programs"
IF=$(route -n get default | awk '/interface:/{print $2}'); case "$IF" in ""|utun*) IF=en0;; esac
LOCAL=$(ipconfig getifaddr $IF)

echo "2b. no program of the customer's gets around Leyra on the network card (what a browser's WebRTC does)"
BYPASS=$(curl -s -o /dev/null -w '%{http_code}' --noproxy '*' --interface $LOCAL --max-time 8 https://1.1.1.1/cdn-cgi/trace)
echo "   bound to the network card ($IF $LOCAL): $BYPASS (must fail)"; [ "$BYPASS" = "200" ] && fail "a program bound to the network card got around Leyra"

echo "2c. the Claude desktop app (stand-in: a program inside Claude.app) only ever leaves through the exit"
FAKE=/tmp/LeyraFake; rm -rf $FAKE; mkdir -p "$FAKE/Claude.app/Contents/MacOS" "$FAKE/Google Chrome.app/Contents/MacOS"
cp /usr/bin/curl "$FAKE/Claude.app/Contents/MacOS/Claude"; cp /usr/bin/nc "$FAKE/Google Chrome.app/Contents/MacOS/Google Chrome"
CA=$("$FAKE/Claude.app/Contents/MacOS/Claude" -s --noproxy '*' --max-time 20 https://1.1.1.1/cdn-cgi/trace | sed -n 's/^ip=//p')
CB=$("$FAKE/Claude.app/Contents/MacOS/Claude" -s -o /dev/null -w '%{http_code}' --noproxy '*' --interface $LOCAL --max-time 8 https://1.1.1.1/cdn-cgi/trace)
EXIT=$(curl -s --max-time 25 -x http://127.0.0.1:17890 https://claude.ai/cdn-cgi/trace | sed -n 's/^ip=//p')
echo "   Claude app to any site, no proxy: seen as $CA (exit $EXIT); bound to the network card: $CB (must fail)"
[ -n "$CA" ] && [ "$CA" = "$EXIT" ] || fail "the Claude app reached a non-Claude site without the exit: $CA"
[ "$CB" = "200" ] && fail "the Claude app got around Leyra on the network card"

echo "2d. browsers' UDP (WebRTC) is refused; other programs' UDP still works"
STUN='\x00\x01\x00\x00\x21\x12\xa4\x42\x4c\x65\x79\x72\x61\x54\x65\x73\x74\x30\x30\x31'
BR=$(printf "$STUN" | "$FAKE/Google Chrome.app/Contents/MacOS/Google Chrome" -u -w 4 stun.l.google.com 19302 2>/dev/null | wc -c | tr -d ' ')
OT=$(printf "$STUN" | /usr/bin/nc -u -w 4 stun.l.google.com 19302 2>/dev/null | wc -c | tr -d ' ')
echo "   STUN answer bytes: browser $BR (must be 0), other program $OT"
[ "$BR" = "0" ] || fail "a browser's UDP (WebRTC) got an answer in coexist mode"
[ "$OT" != "0" ] || echo "   (other program got no STUN answer either: network may block UDP, not a Leyra failure)"

echo "3. the listed site goes through the Leyra exit, others through the customer's own proxy"
L=$(curl -s --max-time 25 -x http://127.0.0.1:17890 https://api.ipify.org)
O=$(curl -s --max-time 25 -x http://127.0.0.1:17890 https://icanhazip.com | tr -d '\n')
echo "   listed: $L, other site: $O (real $REAL); own proxy handled: $(sort -u $LOG | tr '\n' ' ')"
[[ "$L" =~ ^[0-9.]+$ && "$L" != "$REAL" ]] || fail "listed site did not go through the exit: $L"
grep -q icanhazip $LOG || fail "other sites were not handed to the customer's own proxy"
grep -q ipify $LOG && fail "the listed site went through the customer's proxy instead of the exit"

echo "3b. Claude always through the exit (not on the list), also while the line is down"
C=$(curl -s --max-time 25 -x http://127.0.0.1:17890 https://claude.ai/cdn-cgi/trace | sed -n 's/^ip=//p')
echo "   claude.ai sees $C (exit $L)"; [ "$C" = "$L" ] || fail "Claude did not go through the exit in coexist mode: $C"
sudo pkill -9 -x mihomo; OTHER=""; end=$((SECONDS+25))
while [ $SECONDS -lt $end ]; do
  ip=$(curl -s --max-time 5 -x http://127.0.0.1:17890 https://claude.ai/cdn-cgi/trace | sed -n 's/^ip=//p')
  [ -n "$ip" ] && [ "$ip" != "$L" ] && OTHER="$OTHER $ip"; sleep 0.5
done
HC=$("$FAKE/Claude.app/Contents/MacOS/Claude" -s --noproxy '*' --max-time 8 https://1.1.1.1/cdn-cgi/trace | sed -n 's/^ip=//p')
[ -n "$HC" ] && [ "$HC" != "$L" ] && OTHER="$OTHER app:$HC"
echo "   other addresses while the line was down:${OTHER:- none}"; [ -z "$OTHER" ] || fail "while the line was down Claude went out through another address:$OTHER"
HOWN=$(curl -s -o /dev/null -w '%{http_code}' --max-time 20 -x http://127.0.0.1:7890 https://www.apple.com/)
echo "   the customer's own proxy while the line was down: $HOWN"; [ "$HOWN" = "200" ] || [ "$HOWN" = "301" ] || fail "the customer's own proxy stopped while the line was down: $HOWN"
s=$(wait_for '^protected' 150) || fail "coexist protection did not come back after the line broke: $s"

echo "4. the customer's proxy app sets itself back as system proxy: Leyra takes it again"
echo "$SVC" | while read -r s; do sudo networksetup -setwebproxy "$s" 127.0.0.1 7890; done
end=$((SECONDS+20)); until proxyof | grep -q "Port: 17890" || [ $SECONDS -gt $end ]; do sleep 2; done
proxyof | grep -q "Port: 17890" && echo "   taken again" || fail "Leyra did not take the system proxy back"

echo "5. pause: the customer's own system proxy is back exactly"
ask '{"type":"pause","minutes":10}' >/dev/null; wait_for '^paused' 30 >/dev/null || fail "did not pause"
echo "   after pause: $(proxyof)"
proxyof | grep -q "Port: 7890" || fail "the customer's system proxy was not restored"
PA=$("$FAKE/Claude.app/Contents/MacOS/Claude" -s -o /dev/null -w '%{http_code}' --noproxy '*' --interface $LOCAL --max-time 10 https://1.1.1.1/cdn-cgi/trace)
echo "   after pause programs connect normally again: $PA"; [ "$PA" = "200" ] || fail "the lock stayed after pausing Leyra: $PA"
rm -rf $FAKE

echo "6. the customer's proxy app uses a PAC script (Clash's PAC mode): Leyra recognises it and puts it back exactly"
mkdir -p /tmp/leyrapac && echo "function FindProxyForURL(u,h){return 'PROXY 127.0.0.1:7890; DIRECT';}" > /tmp/leyrapac/proxy.pac
python3 -m http.server 18181 --bind 127.0.0.1 --directory /tmp/leyrapac >/dev/null 2>&1 & PAC=$!
end=$((SECONDS+30)); until curl -s --noproxy '*' --max-time 3 -o /dev/null http://127.0.0.1:18181/proxy.pac || [ $SECONDS -gt $end ]; do sleep 1; done
echo "   PAC script served after $((SECONDS-end+30)) s"
echo "$SVC" | while read -r s; do sudo networksetup -setwebproxystate "$s" off; sudo networksetup -setsecurewebproxystate "$s" off; sudo networksetup -setautoproxyurl "$s" http://127.0.0.1:18181/proxy.pac; sudo networksetup -setautoproxystate "$s" on; done
ask '{"type":"resume"}' >/dev/null
CO=""; end=$((SECONDS+90)); until [ "$CO" = "True" ] || [ $SECONDS -gt $end ]; do sleep 3; CO=$(python3 ask.py '{"type":"status"}' --full 2>/dev/null | python3 -c 'import json,sys;v=json.load(sys.stdin)["value"];print(v.get("coexist") and v["protection"]["state"]=="protected")' 2>/dev/null); done
echo "   with a PAC script as the system proxy: sharing with the customer's proxy = $CO"
[ "$CO" = "True" ] || fail "a PAC script on this Mac was not recognised as the customer's own proxy"
ask '{"type":"pause","minutes":10}' >/dev/null; wait_for '^paused' 30 >/dev/null || fail "did not pause"
AUTO=$(networksetup -getautoproxyurl "$(echo "$SVC" | head -1)" | tr '\n' ' ')
echo "   after pause the customer's PAC setting: $AUTO"
echo "$AUTO" | grep -q "proxy.pac" && echo "$AUTO" | grep -q "Enabled: Yes" || fail "the customer's PAC setting was not put back: $AUTO"
echo "$SVC" | while read -r s; do sudo networksetup -setautoproxystate "$s" off; done
kill $PAC 2>/dev/null
kill $OWN 2>/dev/null
if [ ${#FAIL[@]} -gt 0 ]; then echo "LEYRA_MAC_COEXIST=FAIL (${#FAIL[@]})"; exit 1; fi
echo "LEYRA_MAC_COEXIST=PASS"
