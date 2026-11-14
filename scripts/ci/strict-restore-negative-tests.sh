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

# Exercise the real verify command against a clean, isolated Git history.
(
    fixture_repo="$run_dir/topology"
    fixture_contract="$run_dir/strict-restore-fixture.sh"
    mkdir -- "$fixture_repo" "$run_dir/no-hooks"
    cd -- "$fixture_repo"
    git init -q --template= -b c1
    git config user.name 'Strict Restore fixture'
    git config user.email 'fixture@example.invalid'
    git config commit.gpgSign false
    git config core.autocrlf false
    git config core.hooksPath "$run_dir/no-hooks"

    git commit -qm 'Fixture base' --allow-empty
    fixture_base=$(git rev-parse HEAD)
    git switch -qc accepted
    mapfile -t preserved_locks < <(
        git -C "$root" ls-tree -r --name-only "$ACCEPTED_SOURCE" |
            awk -F / '$NF == "packages.lock.json"'
    )
    [[ ${#preserved_locks[@]} == 12 ]]
    git -C "$root" archive "$ACCEPTED_SOURCE" -- "${preserved_locks[@]}" | tar -xf -
    git add --all
    git commit -qm 'Fixture accepted source'
    fixture_source=$(git rev-parse HEAD)

    mapfile -t graph_files < <(
        git -C "$root" ls-tree -r --name-only "$ACCEPTED_FINAL" |
            awk -F / '$NF ~ /[.]csproj$/ || $NF ~ /^packages.*[.]lock[.]json$/'
    )
    [[ ${#graph_files[@]} == 78 ]]
    git -C "$root" archive "$ACCEPTED_FINAL" -- "${graph_files[@]}" "$PROJECT_MANIFEST" \
        "$LOCK_MANIFEST" "$STRICT_CONFIG" scripts/ci/strict-restore-graph.py zeka-web-api.sln | tar -xf -
    git add --all
    git commit -qm 'Fixture accepted overlay'
    fixture_overlay=$(git rev-parse HEAD)
    fixture_tree=$(git rev-parse 'HEAD^{tree}')
    git commit -qm 'Fixture accepted final lineage' --allow-empty
    fixture_final=$(git rev-parse HEAD)
    # Keep every production check. Substitute only the synthetic Git object identities.
    sed -e "s/^readonly ACCEPTED_BASE=.*/readonly ACCEPTED_BASE=$fixture_base/" \
        -e "s/^readonly ACCEPTED_SOURCE=.*/readonly ACCEPTED_SOURCE=$fixture_source/" \
        -e "s/^readonly ACCEPTED_OVERLAY_COMMIT=.*/readonly ACCEPTED_OVERLAY_COMMIT=$fixture_overlay/" \
        -e "s/^readonly ACCEPTED_OVERLAY=.*/readonly ACCEPTED_OVERLAY=$fixture_tree/" \
        -e "s/^readonly ACCEPTED_FINAL=.*/readonly ACCEPTED_FINAL=$fixture_final/" \
        "$contract" > "$fixture_contract"

    verify_fixture() {
        local fixture_head
        fixture_head=$(git rev-parse HEAD)
        EXPECTED_HEAD=$fixture_head GITHUB_ACTIONS=true bash "$fixture_contract" verify "$fixture_head"
    }

    git switch -q c1
    git commit -qm 'Independent C1 work' --allow-empty
    c1_before=$(git rev-parse HEAD)
    git merge -q --no-ff --no-edit accepted
    [[ $(git rev-parse HEAD^1) == "$c1_before" && $(git rev-parse HEAD^2) == "$fixture_final" ]]
    git switch -q --detach HEAD
    verify_fixture

    git switch -qc develop "$fixture_base"
    printf 'Independent develop work\n' > develop-fixture.txt
    git add -- develop-fixture.txt
    git commit -qm 'Advance develop independently'
    develop_head=$(git rev-parse HEAD)
    git switch -q c1
    git merge -q --no-ff --no-edit develop
    [[ $(git rev-parse HEAD^2) == "$develop_head" ]]
    git switch -q --detach HEAD
    verify_fixture

    newer_lock=Tools/Zeka.AuthOwnerProvision/packages.linux-x64.lock.json
    if git cat-file -e "$fixture_source:$newer_lock" 2>/dev/null; then
        fail 'fixture lock must be newer than the accepted source'
    fi
    printf '\n' >> "$newer_lock"
    git add -- "$newer_lock"
    git commit -qm 'Unapproved change to a newer lock'
    must_fail verify_fixture
    grep -Fxq "strict-restore: lock hash mismatch: $newer_lock" "$run_dir/result"

    # Verify the coordinated manifest and lock really agree before testing trust.
    updated_hash=$(hash_file "$newer_lock")
    awk -v lock="$newer_lock" -v digest="$updated_hash" \
        '$2 == lock { print digest "  " lock; next } { print }' "$LOCK_MANIFEST" > "$run_dir/updated-manifest"
    mv -- "$run_dir/updated-manifest" "$LOCK_MANIFEST"
    grep -Fxq "$updated_hash  $newer_lock" "$LOCK_MANIFEST"
    sha256sum --check --status "$LOCK_MANIFEST"
    git add -- "$LOCK_MANIFEST"
    git commit -qm 'Coordinated change to lock and its hash manifest'
    must_fail verify_fixture
    grep -Fxq 'strict-restore: lock manifest differs from the accepted authority' "$run_dir/result"

    unrelated=$(git commit-tree "$fixture_tree" -p "$fixture_base" -m 'Same contents without accepted history')
    git switch -q --detach "$unrelated"
    must_fail verify_fixture
    grep -Eq 'accepted (overlay|final lineage) is absent from checkout history' "$run_dir/result"

    git switch -q --detach "$fixture_overlay"
    must_fail verify_fixture
    grep -Fxq 'strict-restore: accepted final lineage is absent from checkout history' "$run_dir/result"
)
printf 'PASS: strict restore offline negative tests\n'
