# Pro Guitar Deep Scan

Infers the six-string tuning used by a Pro Guitar chart by comparing chart notes
against the song's guitar stem.

The scanner is opt-in and **dry-run by default**. It does not modify `song.ini`
unless `--write` is passed, and it refuses to write when the tuning is ambiguous
unless a candidate is forced.

## In-game button

`Settings → Songs → Deep Scan` runs the same analyzer for every Pro Guitar song
that does not already have a tuning value. It writes confident results, leaves
ambiguous songs unchanged, and refreshes the song cache. The button sits
immediately left of `Scan Songs`.

## Usage

```powershell
# Dry run over a song folder or a library root
dotnet run --project scripts/DeepScan -- "K:\Songs"

# Write the machine-readable report
dotnet run --project scripts/DeepScan -- "K:\Songs" --json deepscan.json

# Write a confident result into song.ini
dotnet run --project scripts/DeepScan -- "K:\Songs\Some Song" --write

# Force a candidate when low-string evidence is missing
dotnet run --project scripts/DeepScan -- "K:\Songs\Some Song" --tuning "Drop Bb" --write
```

## Output

The scanner writes:

```ini
yarg_real_guitar_tuning = 34 41 46 51 55 60
yarg_real_guitar_tuning_cents = 0
```

The values are absolute open-string MIDI notes, low E to high E. YARG reads
this key during song scanning and uses it for Real Guitar pitch matching.

The in-game button refreshes the song cache automatically after writing. After
using the command line tool, run a full song-cache refresh in YARG (or delete
`songcache.bin`) so the new tuning value is picked up.

## Method

1. Parse the Pro Guitar 22-fret track, then the 17-fret track.
2. Pick the first audible stem: `guitar.ogg`, then `rhythm.ogg`, then
   `song.ogg`. A stem is skipped when it is absent, cannot be decoded, or is
   silent across representative song sections. The scan audio is decoded raw
   as float samples; no equalizer, EQ, or other DSP is applied.
3. Order chart notes using chart information only. A greedy decision tree
   measures how many tuning candidates each note or chord separates, so the
   first notes are the most informative rather than the first notes in time.
   The ordering then fills out string and song-section coverage.
4. Scan at least six notes. After the sixth, stop as soon as the leading
   candidate is confident. If the result is not confident, continue through
   the ordered chart notes, including a full-song fallback when necessary.
5. Each scan window decodes only about 0.45 s of audio around the note. The
   monophonic pass measures the lowest chart string; the polyphonic
   `ChordAnalyzer` samples the stable note mask after a chart chord attack.
6. Score common tuning templates against the monophonic pitch samples and
   chord masks.  Chord evidence is matched by exact MIDI note, not just
   pitch class, so low-string harmonics do not make a down-tuned chord look
   like E Standard.
7. Write only when there is enough evidence and the best candidate is clearly
   ahead of the runner-up, either globally or on a decisive string. A forced
   `--tuning` short-circuits the confidence check after the six-note minimum.

The scanner reports monophonic and chord evidence separately in its output and
JSON report.  If a string appears only in a silent or masked chord, the result
can still be ambiguous; the scanner reports that instead of guessing.
