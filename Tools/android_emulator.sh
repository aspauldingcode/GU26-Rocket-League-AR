#!/bin/zsh
# Build for Android Emulator (ARM64 + x86_64), boot an AVD if needed, install and launch.
#   zsh Tools/android_emulator.sh
#   zsh Tools/android_emulator.sh --ei demo 0
#   ANDROID_AVD=Pixel_6 zsh Tools/android_emulator.sh
#
# Prefers an arm64-v8a Google APIs / Play image on Apple Silicon. 6DoF/passthrough need ARCore
# (install Google's ARCore for Emulator APK, or use a Play-store system image). Default launch is
# 3DoF so the app is usable without a camera / ARCore session.
set -u
P=${0:A:h:h}
cd $P || exit 1

SDK=${ANDROID_HOME:-${ANDROID_SDK_ROOT:-$HOME/Library/Android/sdk}}
EMULATOR_BIN=$SDK/emulator/emulator
ADB_BIN=${ADB:-adb}
export PATH="$SDK/platform-tools:$SDK/emulator:${PATH}"

export CARDBOARDXR_EMULATOR=1
export CARDBOARDXR_ANDROID_ARCHS=emulator

# Default to 3DoF when the caller passed no launch options.
if [ $# -eq 0 ]; then
    set -- --ei dof 3
fi

emulator_serial() {
    $ADB_BIN devices 2>/dev/null | awk '/^emulator-/{print $1; exit}'
}

wait_for_boot() {
    local serial=$1
    local i
    echo "== waiting for $serial to finish booting"
    for i in {1..120}; do
        boot=$($ADB_BIN -s $serial shell getprop sys.boot_completed 2>/dev/null | tr -d '\r')
        if [ "$boot" = "1" ]; then
            echo "== emulator ready"
            return 0
        fi
        sleep 2
    done
    echo "Emulator did not finish booting in time"
    return 1
}

SERIAL=$(emulator_serial)
if [ -z "$SERIAL" ]; then
    if [ ! -x $EMULATOR_BIN ]; then
        echo "No running emulator, and $EMULATOR_BIN missing."
        echo "Install Android SDK Emulator, create an AVD (arm64-v8a Google APIs on Apple Silicon),"
        echo "or start one from Android Studio, then re-run."
        exit 1
    fi
    AVD=${ANDROID_AVD:-}
    if [ -z "$AVD" ]; then
        # Prefer an arm64 image name; otherwise the first AVD.
        AVD=$($EMULATOR_BIN -list-avds 2>/dev/null | awk '/arm64/{print; exit}')
        if [ -z "$AVD" ]; then
            AVD=$($EMULATOR_BIN -list-avds 2>/dev/null | head -1)
        fi
    fi
    if [ -z "$AVD" ]; then
        echo "No Android Virtual Devices found. Create one in Android Studio (Device Manager),"
        echo "then: ANDROID_AVD=Your_AVD_Name zsh Tools/android_emulator.sh"
        exit 1
    fi
    echo "== starting AVD $AVD"
    $EMULATOR_BIN -avd "$AVD" -netdelay none -netspeed full >/tmp/cardboardxr-emulator.log 2>&1 &
    for i in {1..90}; do
        SERIAL=$(emulator_serial)
        [ -n "$SERIAL" ] && break
        sleep 2
    done
    if [ -z "$SERIAL" ]; then
        echo "Emulator did not appear in adb devices; see /tmp/cardboardxr-emulator.log"
        exit 1
    fi
fi

export CARDBOARDXR_ANDROID_SERIAL=$SERIAL
wait_for_boot $SERIAL || exit 1

echo "== device $SERIAL (emulator build: ARM64 + x86_64, launch: $*)"
zsh $P/Tools/build_and_run.sh "$@"
