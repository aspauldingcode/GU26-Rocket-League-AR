#!/bin/zsh
# Runs the EditMode tests, builds the app (Core, the lobby and the demos), installs it on the phone and launches it, then
# prints ~30 s of the app's tagged logs, the display's refresh-rate votes and the phone's
# temperatures. Extra arguments go to `am start`, for launch options:
#   zsh Tools/build_and_run.sh --ei dof 6 --ei passthrough 1
# Options: demo N, dof 3|6, passthrough 1, arView 1, boundaryCm N, handAxes 1..8, pinchStudy 1,
# imuPosition 0, nativeImu 0, tracker 1, predictionMs N, fps N.
# For heat: renderScale N (percent, e.g. 70), msaa 1|2|4|8, handHz N (0 = every image).
# Install an already-built APK without rebuilding:
#   zsh Tools/build_and_run.sh --install-only
# If the Editor is already open and Builds/ASCIIRooms.apk exists, the Unity step is skipped
# automatically (same idea as make install / iOS). Set CARDBOARDXR_FORCE_UNITY=1 for a fresh
# batch build (Editor must be closed). Logs go to $LOGS.
# USB drops are common: if install doesn't print "Success", run `adb kill-server; adb start-server`
# and install again, since the launch that follows would otherwise start the previous build.
set -u
P=${0:A:h:h}
# Another Unity 6000.6 install: UNITY=/path/to/Unity.app/Contents/MacOS/Unity zsh Tools/...
U=${UNITY:-/Applications/Unity/Hub/Editor/6000.6.8f1/Unity.app/Contents/MacOS/Unity}
# Another app id (an iOS bundle id belongs to the Apple team that first signed it):
#   CARDBOARDXR_BUNDLE_ID=com.example.gurlar zsh Tools/...
PKG=${CARDBOARDXR_BUNDLE_ID:-com.rocketleague.gurlar}
LOGS=${TMPDIR:-/tmp}/cardboardxr
APK=$P/Builds/ASCIIRooms.apk
# Older builds used the previous product name; reuse that APK if the new path isn't there yet.
LEGACY_APK=$P/Builds/CardboardXR.apk
mkdir -p $LOGS
cd $P || exit 1

resolve_apk() {
    if [ -f "$APK" ]; then
        return 0
    fi
    if [ -f "$LEGACY_APK" ]; then
        APK=$LEGACY_APK
        return 0
    fi
    return 1
}

INSTALL_ONLY=0
LAUNCH_ARGS=()
for arg in "$@"; do
    if [ "$arg" = "--install-only" ]; then
        INSTALL_ONLY=1
    else
        LAUNCH_ARGS+=("$arg")
    fi
done
set -- "${LAUNCH_ARGS[@]}"

# Batchmode can't lock the project while the Editor has it open. Prefer installing the last APK
# so `make install-android` works without quitting Unity (mirrors Tools/ios_build_and_run.sh).
if [ $INSTALL_ONLY -eq 0 ] && pgrep -f "Unity.app/Contents/MacOS/Unity" >/dev/null; then
    if [ "${CARDBOARDXR_FORCE_UNITY:-}" = "1" ]; then
        echo "Unity is running; close it first (CARDBOARDXR_FORCE_UNITY=1 needs the project lock)."
        exit 1
    fi
    if resolve_apk; then
        echo "== Unity is open; reusing $APK (set CARDBOARDXR_FORCE_UNITY=1 for a fresh build)"
        INSTALL_ONLY=1
    else
        echo "Unity is running and there is no Android APK yet at $APK."
        echo "Close Unity once for a batch build (make install-android), or build an APK from the Editor."
        echo "Later installs can leave the Editor open when $APK already exists."
        exit 1
    fi
fi

# Prefer an explicit serial (emulator script sets this), else -e when CARDBOARDXR_EMULATOR=1.
ADB=(adb)
if [ -n "${CARDBOARDXR_ANDROID_SERIAL:-}" ]; then
    ADB=(adb -s "$CARDBOARDXR_ANDROID_SERIAL")
elif [ "${CARDBOARDXR_EMULATOR:-}" = "1" ]; then
    ADB=(adb -e)
fi

usb_android_hint() {
    # macOS sees the phone over USB even when USB debugging is off; adb does not.
    if command -v ioreg >/dev/null 2>&1; then
        ioreg -p IOUSB -w0 2>/dev/null | grep -E 'Android|SAMSUNG_Android|Pixel|OnePlus|Xiaomi|Huawei' || true
    fi
}

adb_ready() {
    adb start-server >/dev/null 2>&1
    if [ -n "${CARDBOARDXR_ANDROID_SERIAL:-}" ]; then
        "${ADB[@]}" get-state 2>/dev/null | grep -q device
        return $?
    fi
    if [ "${CARDBOARDXR_EMULATOR:-}" = "1" ]; then
        "${ADB[@]}" get-state 2>/dev/null | grep -q device
        return $?
    fi
    adb devices 2>/dev/null | awk 'NR>1 && $2=="device" {found=1} END{exit found?0:1}'
}

describe_adb() {
    echo "adb devices:"
    adb devices -l
    local usb
    usb=$(usb_android_hint)
    if [ -n "$usb" ]; then
        echo "USB Android is plugged in, but ADB can't talk to it yet:"
        echo "$usb" | sed 's/^/  /'
        echo "On the phone: unlock → Developer options → USB debugging ON."
        echo "Pull down the USB notification → File transfer / MTP (not Charging only)."
        echo "Accept the 'Allow USB debugging?' prompt if it appears."
    else
        echo "No Android phone/emulator is visible to ADB."
        echo "Plug in a phone with USB debugging, or: make android-emu"
    fi
}

# Wait for a usable device. Default 120 s after the build so a late USB-debug prompt can be accepted.
wait_for_adb() {
    local seconds=${1:-120}
    local i state
    if adb_ready; then
        return 0
    fi
    echo "== waiting up to ${seconds}s for an ADB device"
    describe_adb
    for i in $(seq 1 $seconds); do
        sleep 1
        if adb_ready; then
            echo "== ADB device ready"
            adb devices -l
            return 0
        fi
        # Re-probe every 15 s in case the phone flipped into debugging mode.
        if (( i % 15 == 0 )); then
            adb kill-server >/dev/null 2>&1
            adb start-server >/dev/null 2>&1
            echo "  …still waiting (${i}s). Unlock the phone and accept USB debugging if prompted."
        fi
    done
    describe_adb
    return 1
}

install_and_launch() {
    if ! resolve_apk; then
        echo "Missing $APK — build first (make install-android with Unity closed once)."
        exit 1
    fi
    if ! wait_for_adb "${CARDBOARDXR_ADB_WAIT:-120}"; then
        echo "Install failed; not launching."
        echo "Fix USB debugging, then reuse this APK with: make install-android-apk"
        echo "Or use an emulator: make android-emu"
        exit 1
    fi

    echo "== install (${ADB[*]} → $APK)"
    # Stop it first: installing over the running app and then launching crashes Unity's activity.
    "${ADB[@]}" shell am force-stop $PKG >/dev/null 2>&1 || true
    # Stop unless it installs: launching anyway would start the build already on the phone, and its logs
    # would look like the new one's. USB drops are common, so this happens.
    local INSTALL
    INSTALL=$("${ADB[@]}" install -r $APK 2>&1 | tee /dev/stderr | tail -1)
    case "$INSTALL" in
        *Success*) ;;
        *)
            echo "Install failed; not launching. Run 'adb kill-server; adb start-server', check 'adb devices', then:"
            echo "  make install-android-apk"
            exit 1
            ;;
    esac
    "${ADB[@]}" logcat -c
    "${ADB[@]}" shell am start -W -n $PKG/com.unity3d.player.UnityPlayerGameActivity "$@" | grep -E "Status|LaunchState"
    sleep 32
    "${ADB[@]}" logcat -d -s Unity:V AndroidRuntime:E | grep -E "\[Demos\]|\[AR\]|\[Pose\]|\[Room\]|\[Anchor\]|\[Hands\]|\[Pinch\]|\[Perf\]|\[Passthrough\]|\[Frames\]|\[Hitch\]|\[Stereo\] (Head|Preferred)|Exception|FATAL" | grep -v AssetPackManager | cut -c1-300 | head -40
    echo "== display votes"
    "${ADB[@]}" shell dumpsys display 2>/dev/null | grep -A14 "mVotesByDisplay" | grep -E "PRIORITY" | cut -c1-160 | head -10 || true
    echo "== temperatures"
    "${ADB[@]}" shell dumpsys thermalservice 2>/dev/null | sed -n '/Current temperatures from HAL:/,/Current cooling/p' | grep -oE "mValue=[0-9.]+, mType=[0-9]+, mName=[A-Za-z_-]+" | grep -iE "skin|battery|cpu|gpu" | head -8 || true
    echo "== the whole session log: adb pull /storage/emulated/0/Android/data/$PKG/files/session.log"
}

if [ $INSTALL_ONLY -eq 1 ]; then
    install_and_launch "$@"
    exit 0
fi

# Fail before the long Unity build when nothing can receive the APK.
if ! adb_ready; then
    echo "== no ADB device yet; checking before the build"
    describe_adb
    if ! wait_for_adb "${CARDBOARDXR_ADB_WAIT_BEFORE:-45}"; then
        echo "Aborting before Unity build. Enable USB debugging (or start an emulator), then re-run."
        echo "If Builds/ASCIIRooms.apk already exists: make install-android-apk"
        exit 1
    fi
fi

echo "== EditMode tests"
rm -f $LOGS/tests.xml
$U -batchmode -projectPath $P -runTests -testPlatform EditMode -testResults $LOGS/tests.xml -logFile $LOGS/tests.log
if [ ! -f $LOGS/tests.xml ]; then grep -E "error CS|Exception" $LOGS/tests.log | head -20; exit 1; fi
python3 - $LOGS/tests.xml <<'EOF' || exit 1
import sys, xml.etree.ElementTree as ET
root = ET.parse(sys.argv[1]).getroot()
print(f'total={root.get("total")} passed={root.get("passed")} failed={root.get("failed")}')
for case in root.iter('test-case'):
    if case.get('result') == 'Failed':
        print('##', case.get('fullname'), (case.findtext('failure/message') or '').strip()[:400])
sys.exit(0 if root.get('failed') == '0' else 1)
EOF

echo "== build"
zsh $P/Tools/make_videos.sh || exit 1
# Removed first: a build that never runs would otherwise leave the last one's log to report success.
rm -f $LOGS/build.log
$U -batchmode -projectPath $P -buildTarget Android -executeMethod ProjectBootstrap.StereoBuilder.BuildAndroid -logFile $LOGS/build.log
grep -E "\[Build\]|error CS" $LOGS/build.log | head -20
grep -q "Build Succeeded" $LOGS/build.log || exit 1

install_and_launch "$@"
