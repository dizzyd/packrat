#!/usr/bin/env bash
#
# Regenerate the ModDB screenshot set.
#
#   bash scripts/make-shots.sh [user@host]      (default: dizzyd@vsclient.home)
#
# Shots need a bigger window than vstestkit's client template, which runs at 960x600 for
# deterministic visual baselines. The template is swapped for the boot and put straight back;
# it lives in the slot's own checkout, so no other tenant on the box sees it, and it is only
# read at boot anyway.
#
# Shadows stay OFF. Asking for them on this headless GL setup crashes the client at startup
# with "FBO ShadowmapFar: One or more attachment points are not framebuffer attachment
# complete", and a swallowed crash here is invisible: run.sh simply boots its own session with
# the restored template and you get small, particle-free shots that look like success.

set -euo pipefail

HOST="${1:-dizzyd@vsclient.home}"
SLOT=Packrat
TREE="vstestkit-$SLOT"
REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT="$REPO/docs/screenshots"
RAW=/tmp/packrat-shots

# Before syncing, not after: sync-linux refuses to push into a slot with a live session, and a
# session left over from poking at the box by hand is the usual reason this stops dead.
ssh "$HOST" "cd $TREE && bash scripts/stop.sh >/dev/null 2>&1 || true" 2>/dev/null || true

echo "==> syncing mod and shots to $HOST"
( cd "$REPO/../vstestkit" && bash scripts/sync-linux.sh "$HOST" --mod "$REPO/Packrat" >/dev/null )
rsync -a --delete "$REPO/shots/" "$HOST:mods/$SLOT/shots/" --exclude bin --exclude obj

echo "==> booting a client at 1920x1080"
ssh "$HOST" bash -s "$SLOT" "$TREE" <<'REMOTE'
set -euo pipefail
SLOT="$1"; TREE="$2"
cd "$HOME/$TREE"

cp templates/clientsettings.json /tmp/packrat-clientsettings.bak
trap 'cp /tmp/packrat-clientsettings.bak templates/clientsettings.json' EXIT

python3 - <<'PY'
import json
p = "templates/clientsettings.json"
d = json.load(open(p))
d["intSettings"].update({
    "screenWidth": 1920, "screenHeight": 1080,
    "viewDistance": 192, "mipmapLevel": 3,
    "shadowMapQuality": 0,          # see the note at the top of make-shots.sh
})
d["boolSettings"].update({
    "renderParticles": True, "ambientParticles": True, "wavingStuff": True,
})
json.dump(d, open(p, "w"), indent=2)
PY

mkdir -p /tmp/packrat-shots && rm -f /tmp/packrat-shots/*.png

# VSTK_SHOT_DIR is read inside the game process, so it has to be exported for the *boot*.
# Putting it on run.sh does nothing: run.sh reuses the live session, and the game that is
# already up never saw it - the shots then land in the fallback /tmp and the fetch finds
# nothing.
VSTK_SHOT_DIR=/tmp/packrat-shots \
VSTK_EXTRA_MODS="$HOME/mods/$SLOT/Packrat/bin/Debug/Mods" \
VSTK_EXTRA_ORIGINS="$HOME/mods/$SLOT/Packrat/assets" \
  bash scripts/boot.sh --client

# Loud, not silent: if the window did not come up at the size we asked for, the shots would be
# taken against whatever booted instead - and every crop box below is measured against 1080p.
SIZE=$(bash scripts/vstk eval --side client 'return capi.Render.FrameWidth + "x" + capi.Render.FrameHeight;' \
       | python3 -c 'import sys,json; print(json.load(sys.stdin)["result"]["value"])')
echo "    client window: $SIZE"
[ "$SIZE" = "1920x1080" ] || { echo "expected 1920x1080" >&2; exit 1; }
REMOTE

echo "==> taking the shots"
# Deliberately tolerant: one scene failing should still let the others be collected, and the
# summary line below says plainly whether any did.
SHOTS_OK=1
ssh "$HOST" "cd \$HOME/$TREE && bash scripts/run.sh \$HOME/mods/$SLOT/shots \
    --mod \$HOME/mods/$SLOT/Packrat --client --keep" > /tmp/packrat-shotrun.log 2>&1 || SHOTS_OK=0
grep -E "^ok|^FAIL|^ERR|passed," /tmp/packrat-shotrun.log || true

ssh "$HOST" "cd \$HOME/$TREE && bash scripts/stop.sh" >/dev/null 2>&1 || true

echo "==> fetching and cropping"
mkdir -p "$OUT" "$RAW"
# Clear both ends. A scene that has been renamed or dropped otherwise lingers here from a
# previous run and gets cropped into the set again, looking for all the world like it was just
# taken.
rm -f "$RAW"/*.png "$OUT"/*.png
rsync -a "$HOST:$RAW/*.png" "$RAW/"

python3 - "$RAW" "$OUT" <<'PY'
import sys, pathlib
from PIL import Image

raw, out = pathlib.Path(sys.argv[1]), pathlib.Path(sys.argv[2])

# The hotbar and stat bars reopen themselves and cannot be closed for good, so they are cropped
# off: about 153px of a 1080 frame. 1690x909 out of 1920x1080 matches the house style.
CROP = (115, 18, 1805, 927)

# ...except the command shot. The chat panel is anchored to the bottom-left corner, so the usual
# box clips its first character off every line. Same 1690x909, pushed against the left edge and
# dropped to sit just above the health bar.
CHAT = (0, 76, 1690, 985)

for src in sorted(raw.glob("*.png")):
    if src.name.startswith("06-"):
        continue
    im = Image.open(src).crop(CHAT if src.name.startswith("05-") else CROP)
    im.save(out / src.name)
    print(f"    {src.name}  ->  {im.width}x{im.height}")

src = raw / "06-icon-source.png"
if src.exists():
    # A fixed box measured against Shot06's composition. Left of centre and clear of the bottom
    # of the frame, which keeps out the two things that are fine in a gallery shot and wrong in an
    # icon: the crosshair the client draws dead centre, and the health and hotbar strips.
    ICON = (60, 255, 760, 955)
    im = Image.open(src).crop(ICON).resize((480, 480), Image.LANCZOS)
    # A screenshot-derived PNG is nearly 300KB at full colour and half that on a 256-colour
    # palette, with no difference anyone will see at icon size.
    out_icon = out / "06-icon-candidate.png"
    im.convert("P", palette=Image.ADAPTIVE, colors=256).save(out_icon, optimize=True)
    print(f"    06-icon-source.png  ->  {out_icon.name}  480x480 (candidate only)")
PY

ls -la "$OUT"/*.png 2>/dev/null

# Deliberately not written over Packrat/modicon.png. The shipped icon is authored and committed,
# and CakeBuild copies it into the release zip, so a script that quietly replaced it would change
# what every download looks like as a side effect of taking screenshots. Copy it by hand if you
# ever want it:
echo
echo "06-icon-candidate.png is a candidate icon only; it is not wired into the build."

[ "$SHOTS_OK" = 1 ] || { echo; echo "NOTE: at least one scene failed - see /tmp/packrat-shotrun.log"; exit 1; }
