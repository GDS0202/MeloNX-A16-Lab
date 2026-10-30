#!/usr/bin/env bash
# Compile and link against the real iOS SDK. Deliberately no archive, IPA or binary upload.
set -euo pipefail
cd "$(dirname "$0")/.."
mkdir -p a16/out/logs
export DOTNET_CLI_TELEMETRY_OPTOUT=1
dotnet publish src/Ryujinx.Library/Ryujinx.Library.csproj \
  -c Release -r ios-arm64 --self-contained true \
  -p:ExtraDefineConstants=DISABLE_UPDATER | tee a16/out/logs/ios-native-check.log
native_core=src/Ryujinx.Library/bin/Release/net10.0/ios-arm64/native/Ryujinx.Library.dylib
publish_core=src/Ryujinx.Library/bin/Release/net10.0/ios-arm64/publish/Ryujinx.Library.dylib
if [[ -s "$publish_core" ]]; then
  mkdir -p "$(dirname "$native_core")"
  cp "$publish_core" "$native_core"
fi
test -s "$native_core"
xcodebuild -resolvePackageDependencies -project src/MeloNX/MeloNX.xcodeproj \
  -scheme MeloNX -onlyUsePackageVersionsFromResolvedFile | tee a16/out/logs/ios-packages-check.log
xcodebuild build -project src/MeloNX/MeloNX.xcodeproj -scheme MeloNX \
  -configuration Release -destination 'generic/platform=iOS' \
  -derivedDataPath "$PWD/a16/out/check-derived-data" -onlyUsePackageVersionsFromResolvedFile \
  CODE_SIGNING_ALLOWED=NO CODE_SIGNING_REQUIRED=NO CODE_SIGN_IDENTITY='' \
  | tee a16/out/logs/ios-app-check.log
