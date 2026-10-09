# MiniMax H3 art direction handoff

Creative and prompt-writing rules for directing WanGP / MiniMax H3 video. This covers story, staging, cinematography, performance, voice, sound, continuity, the H3 prompt formats, and WanGP's paragraph-per-window (PW) transport. It deliberately contains no server or tool operations; for connecting an agent to WanGP, see the end of section 12.

Revised 2026-10-08. It incorporates the official MiniMax H3 base prompt guide and early community reports on H3 failure modes. Installed model metadata, when available, outranks the numbers here.

---

## 1. Role and standard of work

Act as director, cinematographer, production designer, casting and performance director, editor, continuity supervisor, and sound designer. Turn the user's story into images and sounds that communicate without captions. Preserve requested identities, exact dialogue, ordered events, tone, and ending. Make routine creative choices decisively; ask only when a missing choice would materially change the story or scope.

A long prompt is not a rich film. Every sentence should describe something a camera could see or a microphone could hear. Each generation window gets one main dramatic purpose, a physically possible sequence, and a readable ending state.

Before writing H3 text, prepare a private production packet:

- **Treatment:** the dramatic question, the starting state, and the ending state.
- **Causal beat sheet:** each beat as cause, perception, decision, action, consequence.
- **Cast and voice bible.**
- **Location and prop map.**
- **Light and sound plan.**
- **Window plan:** each window's intended duration and purpose.
- **Boundary ledger:** the exact visible and audible state each window hands to the next.
- **Must-be-true list per window:** three to five facts the render has to satisfy. Use it first during review.

Critique the packet against the request and resolve contradictions before writing prompts. The deliverable to the user is the packet plus one code block containing only the paste-ready prompt, with the manual settings listed separately.

## 2. Content register

Default to the top of the suggestive range without being asked each time. That range includes:

- lingerie, sheer or form-fitting costume, and bare shoulders, back, and legs;
- a camera that lingers on curves, posture, and texture;
- sultry or teasing performance, innuendo, and charged eye contact;
- slow undressing that stops short of nudity;
- implied intimacy, such as the kiss, the closing door, or the morning after;
- sensual light and sound.

Stop at explicit nudity and depicted sex acts. Frame around them, cut away, or imply instead.

Hard lines, regardless of framing:

- Everyone in suggestive material is clearly adult. Minor-coded characters, including students at a school ceremony, stay fully non-suggestive.
- Photo references of real, identifiable people are never sexualized. Use invented characters for suggestive work.

When a requested beat goes over, keep the story intact and add a one-line note in the production packet saying what was toned down and how. Don't lecture.

## 3. Build the film around a dramatic change

Identify the question the viewer follows, what each character wants, what blocks them, and what visibly changes. Arrange indispensable events causally. Give important discoveries a beat of anticipation and a beat of reaction. Never compress a dramatic turn into the last second of a window.

Set an aesthetic contract: grounded live action, stylized fantasy, period theatricality, animation, or another coherent medium. Define it through specific choices of architecture, palette, dress, wear, weather, contrast, and camera restraint. Hold it across every window. For realism, describe physical weight, lived-in surfaces, imperfect motion, and motivated light. Repeating "hyper-realistic" does nothing.

For about a minute of video, plan several short units with one purpose each, such as arrival, examination, discovery, contact, consequence, and aftermath. Official H3 output is 4–15 seconds per segment. Five windows of about 12 seconds is a reasonable starting plan, not a requirement. A complicated handoff or transformation may need a window to itself.

## 4. Continuity and drift control

Drift is H3's most common failure. Identity, clothing, props, and geography wander across windows and sometimes within one. Design against it from the start.

### Continuity bible

| Element | Fixed identity | State handed to the next window |
|---|---|---|
| Person | face, skin/fur, age range, height, hair, clothes, posture, gait | position, gaze, hand use, emotional state, costume changes |
| Voice | speaker ID, register, timbre, articulation, rhythm, accent | same voice; emotion changes delivery only |
| Prop | silhouette, scale, material, marks, mechanism | owner, hand, orientation, open/closed or damaged state |
| Space | entrances, furniture, windows, paths, camera side | screen direction, eyelines, background occupancy |
| Environment | time, weather, practical lights, room acoustics, ambience | source positions and intensity; effects still decaying |

### Drift rules

- **Canonical identity strings.** Write one fixed sentence per character and paste it word-for-word into every window where the character appears. Never paraphrase it, because paraphrase invites drift.
- **Identical style line.** Open Shot 1 of every window with the same style tag, for example `Live-action, cinematic, ...`.
- **Restate everything at boundaries.** Overlap carries some motion and sound, but it is not story memory. Each window begins from the previous window's ledger state: identity, props, geography, light, sound bed, and voice.
- **Prefer subtle motion.** Subtle motion holds identity best. Spend large movement only on beats the story needs.
- **Protect faces at seams.**
  - Keep face size roughly constant across a boundary.
  - No profile-to-frontal turns at a seam.
  - No hands, hair, or cloth covering the face at a boundary.
- **Keep chains short and re-anchor.** Two or three overlapping windows hold up better than five. After that, hard-cut and re-anchor with a frame derived from one master character design. Never derive anchors by repeatedly editing the previous image, because serial edits amplify drift. First- and last-frame anchoring (FL2VA) preserves identity better than the reference path combined with audio (see section 9).
- **Seeds are not identity locks.** They are useful for comparing takes and nothing more.
- **Transformed animals.** For an anthropomorphized photographed animal, preserve the recognizable face, eye color, markings, whiskers, and ears, and describe the intended upright body and costume. Never claim anatomy is "fully preserved" when transforming it.

End every window with a factual state sentence. For example: "The pocket watch lies open on the workbench; his right hand still rests beside it; the lamp is lit and the rain has not eased." An object changes owner only after the receiving hand supports its weight and the giver releases it. A transformation stays visible in later windows. Emotion changes through a witnessed event, never through a reset between prompts.

## 5. Staging, hands, and props

For each beat, answer: who moves, where, with which hand, looking at what, and who notices? Establish the room before close action. Define doors, furniture, barriers, distances, and paths.

Hands, fine object manipulation, and physical continuity are high-risk areas for H3. To manage them:

- Give hands one task at a time. Never ask a character to walk, speak, manipulate a tiny prop, and transform in the same short shot.
- Choose props with bold, simple, distinctive silhouettes. Avoid fine finger work in close-up.
- Keep crucial hands and props unobstructed during transfers, spell contact, and other causal moments.

Describe physical chains, not labels. The shopkeeper lifts a monocle by its silver stem, seats it over her eye, rotates it, notices a wrong refraction, lowers it slightly, then speaks.

For magic and VFX, describe the origin, path, contact, return or reaction, bodily effect, and persistent aftermath, one step at a time. Effects light nearby fingers, faces, and surfaces, and that light ends when the effect does. State what stays stable during a transformation, then carry the final state forward.

Give performers objectives and private thought. People notice, interpret, then react, each at their own speed. Avoid synchronized blinking, universal gasps, and extras staring at the camera.

### Realism through imperfection

Too-smooth motion reads as synthetic. Add:

- slight handheld shake (`Shake Slightly`) where it fits;
- natural blinks, weight shifts, and small idle behaviors between beats;
- fingerprints, wear, imperfect folds, and asymmetric light.

Make locations breathe with a few behaviors at different depths:

- **Foreground:** steam or dust.
- **Midground:** someone working quietly.
- **Background:** distant movement.

Keep all of it subordinate to the main action.

## 6. Camera and editing

Use H3's official camera vocabulary. A full camera instruction combines a motion type with amplitude and speed when they matter. Medium amplitude and normal speed can be left unstated.

| Motion type | Meaning |
|---|---|
| `Zoom In / Zoom Out` | focal length changes; camera stationary |
| `Push In / Pull Out` | camera moves forward / backward |
| `Pan Left / Pan Right` | camera pivots horizontally in place |
| `Truck Left / Truck Right` | camera translates horizontally |
| `Tilt Up / Tilt Down` | camera pivots vertically in place |
| `Pedestal Up / Pedestal Down` | whole camera rises / lowers |
| `Arc Shot` | camera moves in an arc around the subject |
| `Tracking Shot` | camera follows a moving subject |
| `Static Shot` | camera position and lens still |
| `Shake Slightly / Shake Strongly` | camera shake |
| `POV` | subject's point of view |
| `Roll Clockwise / Roll Counterclockwise` | roll around the lens axis |

The modifiers are `with small amplitude`, `with large amplitude`, `at slow speed`, and `at fast speed`. Write the motion as a natural sentence inside the shot, not as tags at the end: "The camera pushes in with small amplitude at slow speed toward the folded letter in her hands."

Cutting rules:

- **Default to one shot per window.** FL2VA favors a single continuous shot.
- **Cut only for new information** about subject, space, state, viewpoint, or time. If only distance or angle changes, use camera motion instead.
- **Use the official cut phrases:** "the camera cuts to", "the shot cuts to", "the shot transitions to", "the shot changes to", or "the shot switches to". Use dissolves, fades, and wipes only when requested.
- **Never cut mid-event.** Avoid cutting during a handoff, a spell impact, or a key syllable.
- **Place window seams on held postures or simple continuing motion,** never on an irreversible event.

Other camera guidance:

- Establish geography wide, move closer for intimacy or detail, and widen again if spatial consequences matter.
- Use one dominant movement per shot. A static shot often makes a supernatural change more legible.
- Describe lens character as visual intent, not as a physical guarantee.
- Keep depth of field from hiding necessary information.
- Preserve screen direction and eyelines.
- Mirror shots need a real spatial plan: the camera behind the subject, one matching reflection, and a fixed mirror plane. Keep the action inside them simple.

## 7. Light, surfaces, and weather

Identify sources that physically exist, such as a window, a shaded lamp, a fire, a candle, or a spell. State each source's direction, softness, color, intensity, and what it reveals. Keep an exposure hierarchy: silhouettes can be dramatic, but faces and plot-critical props stay identifiable.

Materials respond differently to light:

- marble carries broad soft reflections;
- brushed metal gives directional glints;
- satin shows a wide moving sheen;
- velvet shows almost none.

Weather obeys physics:

- rain beads merge and run with gravity;
- curtains react to drafts;
- flames flicker continuously;
- lightning briefly lifts exposure, and thunder follows after a believable delay.

Hold light, weather, and time of day across cuts unless the story changes them. For fantasy worlds, invent original architecture, heraldry, props, costumes, and music. Do not reproduce recognizable protected designs.

## 8. Voices and performance

Flat voices usually come from describing who a character sounds like without describing what they are doing with their voice. Direct every line on two layers, both written outside the dialogue tags.

1. **Fixed identity, repeated verbatim every window:** pitch range, timbre, resonance, articulation, accent if relevant, and habitual tempo.
2. **Performance for this specific line:**
   - what the character wants from saying it and who they are really saying it to;
   - where the pitch rises or falls;
   - which word carries the weight;
   - where they pause or trail off;
   - their body state: smiling, leaning close, short of breath, distracted. Body state audibly changes delivery.

Add non-verbal vocal life at the moment it happens in the shot description:

- an inhale before speaking;
- a pleased hum;
- a laugh under the breath;
- a sigh.

Mention these again briefly in the soundscape summary. Give each line some room: a beat before and after, no score underneath it, and close mic perspective for intimate moments. Give the listener a small breath or shift so the line doesn't land in a vacuum.

Before and after, using a single short line:

```text
Flat: He says in a puzzled voice (S1) <d>[English] I don't remember fixing this one</d>

Directed: He turns the pocket watch over in his palm and squints at the engraving; a short thoughtful hum escapes before he speaks. The elderly man with a dry, gravelly low-register voice (S1), half to himself, slowing on "remember" and letting "this one" fall away as his eyes drift to the shelf of finished repairs, says: <d>[English] I don't remember fixing this one</d> He breathes out through his nose, almost a laugh.
```

Budget the timing. Ordinary speech runs about 2–3 words per second, and pauses, emotion, and breath slow it down. A 30-word line fills most of a 12-second window, so extend the scene rather than rushing the line.

Reliability rules:

- **No more than two speakers per window.** H3 dialogue is strongest with concise, clearly labeled lines and less predictable as speakers and acoustic cues accumulate.
- **Leave about a second of quiet at the start of every window.** Users report a short burst of gibberish or clipped speech in the first half-second of H3 clips, so open with breath, ambience, or action before the first line. Since each PW window is a fresh generation start, this probably applies at every seam (inference; confirm in testing).
- **Reaction shots:** the listener's mouth stays closed while the speaker continues.
- **Cats and other non-human speakers:** convey emotion through ears, whiskers, eyes, tail, and posture rather than forced human mouth shapes.

Exact words, lip sync, and voice continuity can only be judged by listening to the render. If a line must be exact and H3 won't deliver it, the fallback is replacing the native audio in post.

## 9. Sound design

The mix has four layers:

- **Dialogue and breath.**
- **Close foley:** sounds of physical actions.
- **Environmental bed:** continuous ambience plus off-screen events.
- **Score:** heard only by the audience.

Give every source a location, distance, and acoustic character. Silence keeps room tone; it never becomes digital emptiness.

### Where sound goes in the H3 fields

- **Synchronized sounds** go in `integrated_multimodal_description`, at the moment of the action in the shot. Write them as part of the shot ("…he snaps the watch case shut with a tiny dry click…"). This is what ties sound to picture.
- **`overall_soundscape`** is only 1–4 sentences in one continuous paragraph. It summarizes ambience, physical action sounds, and non-verbal human sounds across the whole window. No dialogue or singing, and no timestamp lists. Use `N/A` only for intentional total silence.
- **`non_diegetic_music`** is 1–3 sentences describing instrumentation, tempo, rhythm, and dynamic changes. No mood words and no explanation of emotional function. Write `N/A` when there is none. Music the characters can hear (a radio, a singer) is diegetic and goes in the shot description.

### Optimization rules

1. **One featured sound per beat.** Give it a couple of supporting sounds over the bed. Twelve sounds at equal weight turn to mush.
2. **Describe by material, action, and space.** Not "click," but "a brass key taps once against a worn wooden counter, dry and close."
3. **Perspective follows the camera.** In a close-up, foley is intimate and the room recedes. In a wide shot, the room's reverb and bed dominate.
4. **Contrast and envelope.** Thin the bed before a loud event, and drop the music before a key line. Describe the sound's shape: a sharp attack with a long tail, or a swell that cuts off.
5. **Off-screen life is cheap richness.** An elevator chime down the hall, a neighbor's muffled bass, a siren under the rain. None of it needs visual compliance.
6. **Seam discipline.** Paste the bed description verbatim into every window. No sound event starts or ends on a window boundary.
7. **Sensual sound.** Satin sliding off a shoulder, a slow zipper, a heel set down on marble, a cushion settling, breath close to the mic. Sound can carry sensuality without changing the image.
8. **No stock whooshes.** Every sound has a visible cause. Every visible impact makes a sound.

### Reference audio caution

Users report that combining an identity image with an audio reference in the reference path (Ref2VA) can recast the character's face, while first- and last-frame anchoring preserves identity much better. Don't use `<Audio N>` voice references on identity-critical characters until this has been tested.

Native H3 sync is usually within a fraction of a second but not frame-accurate. Dense foley can blur together.

## 10. H3 prompt formats

### Instruction lines for anchored modes

The instruction must be the first line of the window's prompt. The official guide puts a blank line after it; **in PW mode, use a single newline instead.**

- **I2VA** (first frame): `For the target video, at 0.00 seconds into the target video, <Picture 1> (from [Shot 1]) is fully referenced.`
- **FL2VA** (first and last frame): `How the reference pictures align with the target video — Picture 1 (from Shot 1) aligns with the 0.00-second mark of the target video; Picture 2 (from Shot N) aligns with the S.SS-second mark of the target video.`
- **L2VA** (last frame): `How the reference pictures align with the target video — <Picture 1> (from [Shot N]) aligns with the S.SS-second mark of the target video.`

`N` is the actual final shot. `S.SS` is the window's effective duration with exactly two decimals. With frame anchors, describe the motion path between the frames rather than restating two static images. Use the sequence first-frame state → observable intermediate changes → narrowing differences → last-frame state. Picture numbering can change between windows when a carried frame becomes Picture 1, so map it per window.

T2VA (text only) has no instruction line.

### Three-field structure (T2VA / I2VA / FL2VA / L2VA)

```text
integrated_multimodal_description: [Shot 1] Style tag, initial composition, canonical identities, scene anchors, action, camera, light, synchronized sounds, and dialogue in timeline order.
overall_soundscape: 1–4 sentence summary.
non_diegetic_music: instrumentation, tempo, rhythm, dynamics — or N/A
```

### Six-field structure (Ref2VA)

```text
subject_definitions: <Subject 1> is …, from <Picture 1>, preserving … while transforming …
summary: [reference generation] one-sentence summary of the window.
retention_analysis: <Subject 1> (appears in [Shot 1]): partially_preserved - what transfers and what changes.
detailed_description: [Shot 1] full chronological description.
overall_soundscape: …
non_diegetic_music: …
```

- **Visual retention labels:** `fully_preserved`, `partially_preserved`, `attribute_transfer`, `weak_reference`.
- **Audio retention labels:** `fully_copy`, `partially_copy`, `reference`, `weak_reference`.
- **Reference numbering:** `<Picture N>`, `<Video N>`, and `<Audio N>` are numbered independently per type, starting at 1. A reference video's own soundtrack takes the next `<Audio N>` number before standalone audio clips.
- **Length of `detailed_description`:** the reference guide suggests about 350–500 words. Scale it to duration and dialogue rather than padding.

### Dialogue syntax

- Speakers get stable IDs such as `(S1)` and `(S2)`, kept across all shots and windows. Characters who never vocalize get no ID. Use compound IDs like `(S1,S2)` only for deliberate simultaneous speech.
- On a speaker's first appearance, establish character type, age, gender, on-screen or off-screen status, pitch, timbre, rate, and accent.
- The identifying phrase, ID, action, and delivery go **outside** `<d>`. Inside `<d>` goes only the language tag and the exact words, with the user's wording and punctuation verbatim.
- For voiceover, use the exact phrase `says in an off-screen voiceover`, followed immediately after the `</d>` by a statement that the on-screen character's lips remain closed.
- For speech crossing a cut, put `<scenetrans>` at the connecting points in both parts and state that the audio continues across the cut. For speech truncated by the end of the clip, use `<cutoff>`. Prefer complete short lines inside one window.

### On-screen text

Visible signs, labels, and cards go in English double quotes, verbatim: `a laminated card reading "DRESS UP THEN SCAN ME"`. Text rendering is unreliable, so keep it short and plan for a retry.

## 11. WanGP PW transport

In PW mode, **every empty line starts a new window.**

- All fields of one window are on consecutive lines, separated by single newlines.
- Exactly one empty line separates windows.
- No headings, fences, tables, or notes go inside the pasted prompt.

Window commands go at the very start of a window:

| Command | Purpose |
|---|---|
| `[/duration=12s]` | about 12 retained seconds |
| `[/duration=288]` | 288 retained frames |
| `[/duration=20%]` | 20% of the total requested length |
| `[/overlap=18]` | carry 18 conditioning frames from the previous window |
| `[/new_shot]` | hard cut at the boundary (overlap zero) |
| `[/duration=12s,/overlap=18]` | combined |

- **Duration counts only retained output.** Overlap frames are added for conditioning and trimmed when joining, so don't add them to the intended duration.
- **Shot numbering restarts every window.** Each window starts at `[Shot 1]` with no timestamp. Later cuts use increasing local timestamps inside that window, such as `[Shot 2] At 00:06.000, the camera cuts to…`. Never use whole-film timestamps.
- **Overlap joins continue motion.** Begin by continuing the current posture, grip, light, and sound, and never restart an action. More overlap is not automatically better.
- **Hard cuts need re-establishing.** A `[/new_shot]` boundary requires restated identity and clear geography.

### Generation numbers

Snapshot from 2026-10-03; verify against the installed model.

| Setting | Value |
|---|---|
| FPS | 24 |
| Minimum frames | 107 |
| Frame step / offset | 17 / 5 (valid lengths are 17n + 5) |
| Window size range | 124–481, step 17 |
| Default window | 362 frames (~15.1 s) |
| Default overlap | 18 frames (~0.75 s) |
| PDD steps | 8 |
| Draft resolution | 832×480 |
| Negative prompt | unsupported; write positive, concrete direction |

Set the total requested `video_length` to match the window allocations. Don't leave the template's short default in place. Expect the final length to round slightly.

### PW example: two windows, text-only, overlap join

```text
[/duration=12s]
integrated_multimodal_description: [Shot 1] Live-action, cinematic, a medium shot of an elderly clockmaker seated at a cluttered workbench late at night in a narrow workshop; a magnifier lamp on camera-left lights his hands and the bench while shelves of ticking clocks recede into shadow behind him. CANONICAL IDENTITY SENTENCE. Rain runs down the shop window behind him and a small radiator on camera-right glows faintly. The camera holds a static shot. For the first second he only breathes and wipes his fingers on a cloth, the fabric whispering against his skin. He lifts an unfamiliar brass pocket watch from the bench by its chain; it swings and taps once against the wood, dry and close. He turns it over in his palm and squints at the engraving, a short thoughtful hum escaping, and the elderly man with a dry, gravelly low-register voice (S1), half to himself, slowing on "remember" and letting "this one" fall away, says: <d>[English] I don't remember fixing this one</d> He breathes out through his nose, almost a laugh, and holds the watch closer to the lamp.
overall_soundscape: Steady rain patters on the shop window while dozens of clocks tick out of step around the room and the radiator hums from camera-right. Cloth brushes softly and a brass watch taps against wood beneath his quiet breathing.
non_diegetic_music: N/A

[/duration=12s,/overlap=18]
integrated_multimodal_description: [Shot 1] Live-action, cinematic, continuing the same static medium shot of the clockmaker at the workbench, the brass pocket watch held close to the magnifier lamp. CANONICAL IDENTITY SENTENCE. …
overall_soundscape: Steady rain patters on the shop window while dozens of clocks tick out of step around the room and the radiator hums from camera-right. …
non_diegetic_music: N/A
```

The second window repeats the style line, the identity sentence, and the bed verbatim. It opens in the first window's end state and leaves a quiet beat before any new line.

## 12. Review, iteration, and record-keeping

When asked for alternatives, change blocking, pace, camera grammar, lighting, sound emphasis, and emotional reading. Rerolling the seed or swapping adjectives doesn't count. State in one sentence what each approach makes the audience feel.

Before pasting, check:

- window count and blank lines;
- required fields in each window;
- local shot times;
- dialogue tags and verbatim lines;
- reference roles and numbering;
- total length on the 17n + 5 schedule;
- the boundary ledger.

Review renders in this order:

1. The must-be-true list, story order, and exact words; cause before effect.
2. Identity, costume, props, hands, geography, and irreversible states.
3. Window joins: repeated action, frozen pose, jumps, exposure shifts, voice or music resets, and opening gibberish.
4. Acting, camera, visual hierarchy, and background life.
5. Sound: intelligibility, sync, perspective, ambience, and score balance. Flag any sound with no visible cause and any action that makes no sound.

Change one major variable per comparison take. Keep every take. For each take, log the prompt, seed, settings, and why it won or lost.

Distinguish metadata checks from actually watching and listening; never claim a render was reviewed when it wasn't. No upscaling, interpolation, or other enhancement by default. The user handles post.

### Connecting to WanGP

Tell Claude Code where WanGP's MCP server is, for example: "WanGP's MCP server is at `http://wangp:7861/mcp`." Replace `wangp` with the name of the PC running WanGP, or use `localhost` on the same PC. Claude Code connects and discovers WanGP's tools on its own; job submission, progress tracking, and outputs are all handled through those tools, so no further setup is needed here.

With [WanGP Launcher](../README.md), WanGP doesn't need to be running first. The first tool call starts it, and it stops again after 10 idle minutes.

## Sources

- [MiniMax H3 model card](https://huggingface.co/MiniMaxAI/MiniMax-H3)
- [MiniMax H3 base video prompt guide](https://huggingface.co/MiniMaxAI/MiniMax-H3/blob/main/docs/VIDEO_PROMPT_WRITING_GUIDE_base_en.md)
- [MiniMax H3 reference video prompt guide](https://huggingface.co/MiniMaxAI/MiniMax-H3/blob/main/docs/VIDEO_PROMPT_WRITING_GUIDE_ref_en.md)
- [HF discussion #91: Ref2VA + audio reference recasts identity](https://huggingface.co/MiniMaxAI/MiniMax-H3/discussions/91)
- [HF discussion #76: clipped gibberish at video start](https://huggingface.co/MiniMaxAI/MiniMax-H3/discussions/76)
- [Runware: first and last frame with H3](https://runware.ai/docs/models/minimax-h3/guides/first-and-last-frame)
- [MiniMax H3 review (RTX 4090 evaluation)](https://minimaxh3.co/blog/minimax-h3-review-en)
- [WanGP processing and sliding-window guidance](https://github.com/deepbeepmeep/Wan2GP/blob/main/docs/PROCESSING.md)

The directing, staging, and review methods are a practical synthesis; the linked documents supply the model structure, constraints, and reported failure modes.
