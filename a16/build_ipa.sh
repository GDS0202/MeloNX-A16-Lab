#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
if [ "$(uname -s)" != Darwin ]; then
  echo "IPA compilation requires macOS and Xcode; regression checks also run on Linux."
  exit 2
fi
build_root="$PWD/a16/out"
mkdir -p "$build_root/logs"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
variant="${A16_BUILD_VARIANT:-optimized}"
case "$variant" in optimized|control) ;; *) echo "Invalid variant"; exit 2;; esac
xcodebuild -version | tee "$build_root/logs/xcode.log"
dotnet --info > "$build_root/logs/dotnet-info.log"
if [ "$variant" = optimized ]; then
  dotnet run --project tests/A16.Regression/A16.Regression.csproj -c Release | tee "$build_root/logs/regression.log"
fi
dotnet publish src/Ryujinx.Library/Ryujinx.Library.csproj \
  -c Release -r ios-arm64 --self-contained true \
  -p:NativeLib=Shared -p:ExtraDefineConstants=DISABLE_UPDATER \
  | tee "$build_root/logs/native-core.log"
native_core="src/Ryujinx.Library/bin/Release/net10.0/ios-arm64/native/Ryujinx.Library.dylib"
test -s "$native_core"
xcodebuild -resolvePackageDependencies -project src/MeloNX/MeloNX.xcodeproj \
  -scheme MeloNX -onlyUsePackageVersionsFromResolvedFile | tee "$build_root/logs/swift-packages.log"
xcodebuild archive -project src/MeloNX/MeloNX.xcodeproj -scheme MeloNX \
  -configuration Release -destination 'generic/platform=iOS' \
  -archivePath "$build_root/MeloNXA16.xcarchive" -derivedDataPath "$build_root/DerivedData" \
  -onlyUsePackageVersionsFromResolvedFile \
  CODE_SIGNING_ALLOWED=NO CODE_SIGNING_REQUIRED=NO CODE_SIGN_IDENTITY='' \
  | tee "$build_root/logs/xcode-archive.log"
mkdir -p "$build_root/package/Payload"
ditto "$build_root/MeloNXA16.xcarchive/Products/Applications/MeloNX.app" "$build_root/package/Payload/MeloNX.app"
python3 a16/verify_app.py "$build_root/package/Payload/MeloNX.app" "$native_core"
(
 cd "$build_root/package"
 ditto -c -k --keepParent Payload "../MeloNX_A16_Lab_0.1_${variant}_UNSIGNED.ipa"
)
shasum -a 256 "$build_root/MeloNX_A16_Lab_0.1_${variant}_UNSIGNED.ipa" > "$build_root/MeloNX_A16_Lab_0.1_${variant}_UNSIGNED.ipa.sha256"
echo "Unsigned IPA built. Signing with memory entitlement, JIT and device testing still required."
