# GU26 Rocket League AR — build / install helpers
# Wraps Tools/*_build_and_run.sh.
#
#   make              # this help
#   make install      # iPhone: build + install + launch (keeps Unity open when possible)
#   make build        # same as install
#   make install-android / android  # Android phone via adb (needs Unity closed)
#   make android-emu  # Android Emulator (ARM64+x86_64 APK, boots AVD if needed)
#   make ios          # same as install
#   make ios-xcode    # iOS from last Unity export only
#   make ios-fresh    # force Unity re-export (close the Editor first)
#   make launch       # relaunch iOS app without rebuilding
#   make videos       # copy local 360 videos into StreamingAssets
#
# Launch options / overrides:
#   make install ARGS='--ei dof 6 --ei passthrough 1'
#   make install BUNDLE_ID=com.example.gurlar UDID=... TEAM=... DEVELOPMENT=1
#   make android-emu ANDROID_AVD=WawonaEmulator_arm64-v8a_API36
#   make install-android UNITY=/path/to/Unity.app/Contents/MacOS/Unity

SHELL := /bin/zsh
.SHELLFLAGS := -euc
.DEFAULT_GOAL := help

BUNDLE_ID    ?= com.rocketleague.gurlar
DEVELOPMENT  ?=
UNITY        ?=
UDID         ?=
TEAM         ?=
ARGS         ?=

export CARDBOARDXR_BUNDLE_ID := $(BUNDLE_ID)
ifneq ($(strip $(DEVELOPMENT)),)
export CARDBOARDXR_DEVELOPMENT := $(DEVELOPMENT)
endif
ifneq ($(strip $(UNITY)),)
export UNITY
endif
ifneq ($(strip $(UDID)),)
export CARDBOARDXR_UDID := $(UDID)
endif
ifneq ($(strip $(TEAM)),)
export TEAM
endif

.PHONY: help install build run ios ios-xcode ios-fresh install-android android install-android-apk android-emu launch videos release-android

help:
	@printf '%s\n' \
		'make install / build / run   iOS install; reuses last export if Unity is open' \
		'make ios                     same as install' \
		'make ios-xcode               iOS from last export (always skip Unity)' \
		'make ios-fresh               force Unity re-export (close Editor first)' \
		'make install-android / android  Android device: tests → build → install → launch' \
		'make install-android-apk     install+launch existing Builds/CardboardXR.apk (no rebuild)' \
		'make android-emu             Android Emulator (x86_64+ARM64; default --ei dof 3)' \
		'make launch                  relaunch iOS app only' \
		'make videos                  copy local videos into StreamingAssets' \
		'make release-android V=1.2.3 draft GitHub Android release' \
		'' \
		'Options: make install ARGS="--ei dof 6" BUNDLE_ID=... UDID=... TEAM=... DEVELOPMENT=1' \
		'         make install-android ARGS="--ei dof 6" BUNDLE_ID=...' \
		'         make android-emu ANDROID_AVD=Your_AVD_Name ARGS="--ei demo 0"' \
		'Phone must be unlocked for launch. First iOS export still needs Unity closed once.' \
		'Android needs Unity closed for a full rebuild; USB debugging must be on for the phone.'

# Default device path for this fork: iPhone. Script skips Unity when the Editor is open
# and Builds/iOS already exists.
install build run ios:
	zsh Tools/ios_build_and_run.sh $(ARGS)

ios-xcode:
	zsh Tools/ios_build_and_run.sh --xcode $(ARGS)

ios-fresh: _require-closed-unity
	CARDBOARDXR_FORCE_UNITY=1 zsh Tools/ios_build_and_run.sh $(ARGS)

install-android android: _require-closed-unity
	zsh Tools/build_and_run.sh $(ARGS)

# Reuse the APK from the last Android build (skips Unity; phone/emulator must be adb-ready).
install-android-apk:
	zsh Tools/build_and_run.sh --install-only $(ARGS)

android-emu: _require-closed-unity
	zsh Tools/android_emulator.sh $(ARGS)

launch:
	zsh Tools/ios_launch.sh $(ARGS)

videos:
	zsh Tools/make_videos.sh $(ARGS)

# Usage: make release-android V=1.2.3
release-android:
	@test -n "$(V)" || { echo 'Usage: make release-android V=1.2.3'; exit 1; }
	zsh Tools/release_android.sh $(V)

_require-closed-unity:
	@if pgrep -f 'Unity.app/Contents/MacOS/Unity' >/dev/null; then \
		echo 'Unity is running; close it first (this target needs a batchmode project lock).'; \
		exit 1; \
	fi
