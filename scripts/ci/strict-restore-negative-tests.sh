#!/usr/bin/env bash
set -euo pipefail
root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd -P)
contract="$root/scripts/ci/strict-restore.sh"
bash -n "$contract"
run_dir=$(mktemp -d "${RUNNER_TEMP:-${TMPDIR:-/tmp}}/strict-restore-negative.XXXXXX")

must_fail() {
    if ( "$@" ) > "$run_dir/result" 2>&1; then
        printf 'expected rejection: %s\n' "$1" >&2
        exit 1
    fi
}

# The entire finite truth table, including an empty relevance result.
for source in success failure cancelled skipped; do
    for detect in success failure cancelled skipped; do
        for linux in success failure cancelled skipped; do
            for relevance in true false ''; do
                for docker in success failure cancelled skipped; do
                    if [[ $source == success && $detect == success && $linux == success ]] &&
                        { [[ $relevance == true && $docker == success ]] ||
                          [[ $relevance == false && $docker == skipped ]]; }; then
                        bash "$contract" gate "$source" "$detect" "$linux" "$relevance" "$docker" > /dev/null
                    else
                        must_fail bash "$contract" gate "$source" "$detect" "$linux" "$relevance" "$docker"
                    fi
                done
            done
        done
    done
done

cd "$root"
head=$(git rev-parse HEAD)
wrong_head=0000000000000000000000000000000000000000
[[ $head != "$wrong_head" ]]
must_fail bash "$contract" verify "$wrong_head"
grep -q 'checkout SHA differ' "$run_dir/result"
must_fail bash "$contract" relevant "$wrong_head" "$head"
grep -q 'commit missing' "$run_dir/result"
[[ $(bash "$contract" relevant "$head" "$head") == false ]]

cd "$run_dir"
mkdir -p scripts/ci fixture cache
cp -- "$root/scripts/ci/nuget.strict.config" scripts/ci/nuget.strict.config
# Exercise the real verification functions with a minimal temporary fixture.
source "$contract"
verify_config
printf '<packageSourceCredentials />\n' >> "$STRICT_CONFIG"
must_fail verify_config
grep -q 'configuration changed' "$run_dir/result"
sed 's|https://nuget.pkg.github.com/diliwo/index.json|https://example.invalid/v3/index.json|' \
    "$root/scripts/ci/nuget.strict.config" > "$STRICT_CONFIG"
must_fail verify_config
grep -q 'configuration changed' "$run_dir/result"

require_empty_cache cache
touch cache/unexpected
must_fail require_empty_cache cache
grep -q 'nonempty' "$run_dir/result"
must_fail check_credentials
grep -q 'missing or malformed' "$run_dir/result"

# Values are generated offline and never passed to NuGet.
export PACKAGE_USERNAME=fixture-account
export PACKAGE_READ_TOKEN="fixture-$RANDOM-$RANDOM"
check_credentials
(
    export PACKAGE_READ_TOKEN="ghs_${RANDOM}_fixture${RANDOM}.payload${RANDOM}.signature${RANDOM}"
    check_credentials
)
(
    export PACKAGE_READ_TOKEN="fixture-${RANDOM};unexpected=field"
    must_fail check_credentials
    grep -q 'missing or malformed' "$run_dir/result"
)
(
    export PACKAGE_READ_TOKEN=$(printf 'fixture-%s\nunexpected' "$RANDOM")
    must_fail check_credentials
    grep -q 'missing or malformed' "$run_dir/result"
)
(
    export RestoreSources=https://example.invalid/v3/index.json
    must_fail check_credentials
    grep -q 'ambient NuGet source' "$run_dir/result"
)
(
    export NUGET_PACKAGES="$run_dir/cache"
    must_fail check_credentials
    grep -q 'ambient NuGet source' "$run_dir/result"
)
(
    export PACKAGE_USERNAME='fixture;unexpected=field'
    must_fail check_credentials
    grep -q 'missing or malformed' "$run_dir/result"
)
unset PACKAGE_USERNAME PACKAGE_READ_TOKEN

# Missing, changed, and extra on-disk locks must all fail, including ignored files.
lock=fixture/packages.lock.json
printf '{}\n' > "$lock"
LOCK_HASH[$lock]=$(hash_file "$lock")
check_lock_inventory "$lock"
mv -- "$lock" "$lock.original"
must_fail check_lock_inventory "$lock"
grep -q 'inventory differs' "$run_dir/result"
mv -- "$lock.original" "$lock"
printf 'changed\n' >> "$lock"
must_fail check_lock_inventory "$lock"
grep -q 'hash mismatch' "$run_dir/result"
printf '{}\n' > "$lock"
printf 'packages.unexpected.lock.json\n' > .gitignore
git init -q
touch fixture/packages.unexpected.lock.json
git check-ignore -q fixture/packages.unexpected.lock.json
must_fail check_lock_inventory "$lock"
grep -q 'inventory differs' "$run_dir/result"
printf 'PASS: strict restore offline negative tests\n'
