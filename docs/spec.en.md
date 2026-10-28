# MoodWave specification

## 0. Document information

| Item | Value |
|---|---|
| Version | 1.0 |
| Date | 2026-10-26 |
| MoodWave | `main`, with pull request #14 (swipe votes) |
| MIKU | `main` 23d83eb (with pull requests #46, #47 and #49) |
| Language | ASD-STE100 (Simplified Technical English). The Chinese text (`spec.zh-TW.md`) is a translation of this text. |

This document tells how MoodWave operates now.
It replaces the earlier draft plans. Appendix A gives a summary of the decisions in those plans.

If this document and the code do not agree, the code is correct. Then correct this document.

## 1. Terms

| Term | Type | Meaning in this document |
|---|---|---|
| MoodWave | TN | The MIKU module in this repository. Its code name is MoodChannels. |
| MIKU | TN | The music player that loads MoodWave. |
| listener | TN | The person who uses MIKU. |
| library | TN | All local tracks that MIKU knows. |
| track | TN | One audio file in the library. |
| queue | TN | The list of tracks that MIKU plays in sequence. |
| current track | TN | The track that MIKU plays now. |
| queue drawer | TN | The MIKU panel that shows the queue. |
| player bar | TN | The bar at the bottom of the MIKU window. |
| now-playing page | TN | The MIKU page that shows the current track on the full window. |
| channel | TN | A list of tracks with one time of day or one mood. |
| time channel | TN | One of the 5 channels for a time of day. |
| mood channel | TN | One of the 8 channels for a mood. |
| Shuffle All | TN | The channel with all classified tracks. |
| basic analysis | TN | The analysis that finds the channels and the loudness of a track. |
| advanced AI | TN | The function that finds how alike two tracks sound. |
| embedding | TN | The data from the advanced AI that tells how a track sounds. |
| similarity | TN | A number from −1 to 1 that tells how alike two embeddings are. |
| radio | TN | A MoodWave function that plays tracks like one track, album or artist. |
| Smart Playlist | TN | The MoodWave function that adds tracks to the end of the queue. |
| pick | TN | A track that MoodWave selects and adds to the queue. |
| seed | TN | A track that MoodWave compares with other tracks to find the next pick. |
| weight | TN | A number that tells how much a seed counts. |
| vote | TN | A 👍 or a 👎 that the listener gives to a track. |
| play record | TN | A record of one track that played: the time and the seconds played. |
| loudness | TN | How loud a track sounds, in LUFS. |
| `library.db` | TN | The database file of MoodWave. |
| helper | TN | The MoodWave process on macOS (`moodwave-helper`). |
| CLAP process | TN | The process that calculates embeddings (`moodwave-clap`). |

## 2. Overview

### 2.1 What MoodWave does

MoodWave has these functions:

- It analyses the library in the background.
- It puts each track into channels: one time channel and one or two mood channels.
- It measures the loudness of each track. MIKU uses the loudness for ReplayGain.
- With the advanced AI, it starts radios and operates the Smart Playlist.
- It learns from the votes of the listener.

MoodWave does all analysis and all calculations on the computer of the listener.
MoodWave sends no data to a server.
It downloads only the model of the advanced AI, one time.

### 2.2 The two switches

The MoodWave settings have two main switches:

| Switch | Default | Functions |
|---|---|---|
| "啟用 MoodWave" | On | Basic analysis, channels, loudness |
| "啟用進階 AI" | Off | Embeddings, radios, Smart Playlist |

The advanced AI operates only when MoodWave is on.
When the listener turns off MoodWave, MoodWave stops the analysis and removes its page.

### 2.3 Architecture

MoodWave has the same service code (`MoodChannels.Service`) on Windows and on macOS.
The service gets all player data through one interface, `IMoodHost`.

| | Windows | macOS |
|---|---|---|
| Service | In the MIKU process (`MoodChannels.Extension.dll`) | In the helper, a process of its own |
| Connection to MIKU | Direct calls | `miku/extension.js`, one JSON object for each line |
| CLAP process | `moodwave-clap.exe`, started by the service | `moodwave-clap`, started by the helper |
| ffmpeg | The ffmpeg of MIKU | The ffmpeg of MIKU |

The page (`web/moodchannels.js`, `web/moodchannels.css`) is the same on the two systems.

## 3. Background analysis

### 3.1 Basic analysis

The basic analysis does these steps for each track:

1. ffmpeg decodes the track to 16 kHz mono.
2. MoodWave calculates the DSP descriptors (tempo, timbre, key and more).
3. The channel model finds the channels from the descriptors (chapter 4).
4. ffmpeg measures the loudness at the quality of the file (chapter 8).

MoodWave stores the descriptors. Thus a new channel model does not need a new analysis.
A new DSP version makes all stored descriptors old. Then MoodWave analyses all tracks again.

MoodWave does not analyse these tracks:

- A track that is longer than 30 minutes (for example a mix or a full disc in one file).

A track that is shorter than 6 seconds gets no channel.

### 3.2 How MoodWave reads the files

One reader reads each file fully, in large reads, before ffmpeg opens it.
Then the file is in the file cache of the system, and ffmpeg reads it from memory.
Thus a hard disk reads one file at a time and does not move its head between files.

The reader reads at most 1.5 GB before the workers.
The workers and ffmpeg have a priority that is lower than normal.

The setting "CPU 執行緒" in the basic card sets the number of workers.
The default is 1. The listener can change it while the analysis operates.

### 3.3 Advanced AI analysis

The advanced AI calculates an embedding for each track.
The CLAP process cuts 10-second windows from the track.
A track has one window for each full 10 seconds, but not more than 12.
The windows are spread equally from the start to the end of the track.

The setting "每首分析段數" sets how many windows the CLAP process uses:

| Setting | Windows |
|---|---|
| 自動 | 12 with a GPU, 6 on the CPU |
| 6 段 | 6 (fastest) |
| 9 段 | 9 |
| 12 段 | 12 (most accurate) |

The windows of the settings are in each other (6 in 9, 9 in 12).
Thus a higher setting calculates only the windows that it did not calculate before.

On Windows, the CLAP process uses the GPU (DirectML) when the setting "使用 GPU" is on.
If the GPU cannot operate, it uses the CPU.
On macOS, it always uses the CPU.
On the CPU, the setting "CPU 執行緒" in the advanced AI card sets the threads. The default is half of the cores.

The CLAP process starts when there is work. It stops after 1 minute with no work.

### 3.4 Sequence of the analysis

The basic analysis comes first, so that the channels are ready early.

1. The tracks that MIKU will play soon come first, also when the listener paused the analysis.
2. Then the tracks that have no analysis.
3. Then the tracks that have an analysis but no loudness.

The advanced AI analyses only tracks that have a basic analysis. Its sequence is:

1. The tracks that MIKU will play soon.
2. The tracks that the basic analysis did a short time before, while they are in the file cache (only with a GPU).
3. The other tracks, but only when the basic analysis has no work.

Rule 3 prevents two readers on one hard disk at the same time.

The listener can pause each analysis with its own "暫停" button.

### 3.5 Speed limit of the analysis

The setting "限制分析速度" in the basic card limits how fast the analysis reads the disk while a track plays.

| Setting | Limit |
|---|---|
| 4 MB/s | 4 MB each second |
| 8 MB/s | 8 MB each second |
| 16 MB/s | 16 MB each second |
| 無限制 | No limit (the default) |

The limit applies to the reader of the basic analysis and to the advanced AI.
When the limit applies, the reader reads 1 MB at a time and waits between the reads.
Before the CLAP process reads a file, MoodWave reads the file into the file cache at the limit.
When the listener pauses or stops the track, the analysis reads at full speed again.

Use a limit only if the sound stops for short times while the analysis operates.
This can occur when the music is on a USB hard disk.

## 4. Channels

### 4.1 The channels

| No. | Channel | Type | Description |
|---|---|---|---|
| 0 | Morning | Time | 5:00–9:59 |
| 1 | Daytime | Time | 10:00–15:59 |
| 2 | Evening | Time | 16:00–18:59 |
| 3 | Night | Time | 19:00–23:59 |
| 4 | Midnight | Time | 0:00–4:59 |
| 5 | Energetic | Mood | Energetic, strong rhythm |
| 6 | Relax | Mood | Calm |
| 7 | Mellow | Mood | Soft, warm |
| 8 | Upbeat | Mood | Bright, happy |
| 9 | Emotional | Mood | Emotional |
| 10 | Lounge | Mood | Jazz and a relaxed feel |
| 11 | Dance | Mood | Rhythm for dance |
| 12 | Extreme | Mood | Strong, intense |
| 13 | Shuffle All | All | All classified tracks |

### 4.2 How the model finds the channels

The channel model is a set of tree models in `models/basic/`.
For each track, it gives these results:

- Unclassifiable: if the probability of "none" is at the limit or more, the track gets no channel.
- Time channel: the time channel with the highest probability, multiplied by the weight of that channel.
- Mood channels: the mood with the highest weighted probability.
  MoodWave adds the second mood if its weighted probability is at the limit or more.

The weights and the limits are in `models/basic/model.json`.

### 4.3 Changes from the votes

The votes of the listener change the channels (Personalization):

- 👎 on a track in a channel removes the track from that channel.
- 👍 on a track in a channel keeps the track in that channel. No other rule removes it.
- When a channel has 5 or more 👍 and 5 or more 👎, MoodWave trains a model of that channel.
  The model removes tracks with no vote that do not agree with the channel (probability 0.5 or more).
- If a track has no time channel or no mood channel after these changes, it goes to the next channel that the channel model prefers.

The votes only remove tracks from a channel. They never add a track to a channel.
MoodWave trains again after each vote. This takes a few milliseconds.

### 4.4 Built-in tuning

The file `models/basic/tuning.json` has channel models that MoodWave supplies.
The setting "使用內建的頻道調整" turns them on (the default is on).

- A channel that does not have enough votes uses the built-in model.
- For a channel with enough votes, MoodWave trains the model of the channel from the built-in model.
- The votes of the listener always decide their own tracks.

### 4.5 The channel page

The channel page (`#/moods`) has two sections:

- "時段": the time channel of now (a large card), and the other 4 time channels.
- "心情": Shuffle All and the 8 mood channels.

Each card shows the number of tracks. The page also shows how many tracks have an analysis.
When the listener clicks a card, the channel starts (4.6).

### 4.6 Playing a channel

When a channel starts, MoodWave gives two tracks:

1. A random track of the channel. If possible, it is a track with a measured loudness.
2. The next track (rule below).

After this, the channel adds one track at a time.
MIKU asks for the next track when the last track in the queue starts (7.1).

MoodWave selects the next track with this rule:

1. MoodWave calculates the channel again, with all votes until now.
2. It removes the tracks that are in the queue before this point.
3. From the other tracks, it selects one at random. It prefers tracks that did not play in the last 12 hours.
4. If all tracks played in the last 12 hours, it selects the track that played first.

A vote has an effect on the next track immediately, because of step 1.

### 4.7 The channel band

While a channel plays, the player bar shows the channel band.
The band shows the channel name, ▲ and ▼, and 👍 and 👎.

- ▲ and ▼ change to the previous or next channel. The sequence is: Shuffle All, the time channel of now, the 8 mood channels. Channels with no tracks are not in the sequence.
- 👍 and 👎 give a vote on the current track in this channel (4.3).
- 👎 also removes the current track and plays the next track.
- A click on the band opens the now-playing page (7.4).

Shuffle All has no votes.

## 5. Radio

### 5.1 Conditions

The radio operates only when all of these conditions are true:

- The advanced AI is on.
- Its model is on the computer.
- At least one track has an embedding.

When these conditions are true, MIKU menus show the radio items.

### 5.2 The 3 types of radio

| Type | Menu item | Seeds | First track |
|---|---|---|---|
| Track radio | "開始歌曲電台" (track menu) | The track | The track |
| Artist radio | "開始歌手電台：<artist>" (track menu) | The 10 tracks of the artist that played most. If they did not play, 10 at random. | The first seed |
| Album radio | "開始專輯電台" (album menu) | All tracks of the album | The track of the album that played most. If no track played, the first track. |

Each seed has the weight 1.
The radio starts with the first track and one pick.
Then the radio adds one pick at a time (7.1).

In an album radio, the tracks of the album can also be picks. They are at least 6 tracks apart.

### 5.3 Radio votes

Each radio has a set of votes for its start (the track, the album or the artist).
All radios from the same start use the same set.

- 👍 on a seed: MoodWave multiplies the weight of that seed by 1.25.
- 👍 on a track that is not a seed: the track becomes a seed with the weight 0.7.
- 👎 on a track: the track is never a seed and never a pick.
- 👎 on a track: tracks that are very alike (similarity more than 0.9) get a lower score (−0.15).
- 👎 on the current track also removes it and plays the next track.

If all seeds have 👎, MoodWave uses the seeds with no vote change. Thus the radio does not stop.

### 5.4 How MoodWave selects a pick

The radio and the Smart Playlist use this procedure.

MoodWave gives each track in the library a score.
The score of a track is its highest similarity to a seed, multiplied by the weight of that seed.
Thus the track that is most like one seed gets a high score.
The other seeds do not decrease this score.

MoodWave decreases the score in these conditions:

| Condition | Decrease |
|---|---|
| The track played in the last 7 days. | 0.15 (0 after 30 days) |
| The track is very alike to a track that the listener skipped in less than 30 seconds, in the last 3 hours. | 0.10 |
| The same song (another version) played before in this list. | 0.08 for each time |
| The track is very alike to a track with 👎. | 0.15 |

MoodWave does not select these tracks:

- A track in the queue before the pick.
- A seed with the weight 1 or more, except in an album radio.
- A track that has no embedding, or a track that is not in a channel.
- Another version of a seed or of a track that played (the same title and artist).

Then MoodWave applies these rules to the 200 tracks with the highest scores:

1. The artist is not the artist of one of the last 3 tracks.
2. The song is not one of the last 20 tracks.
3. The album is not the album of one of the last 5 tracks.

If no track obeys all 3 rules, MoodWave ignores rule 1.
If no track obeys rules 2 and 3, MoodWave also ignores rule 3.
MoodWave never ignores rule 2.

From the tracks that obey the rules, MoodWave takes the 20 best tracks.
It selects 5 of them at random. A track with a higher score has a higher chance.
From these 5, MoodWave selects the track that follows the current track best.
This is the track with the nearest tempo and key.

The score sets which tracks can become the pick. The transition sets which track becomes the pick.
Section 6.8 gives a full example.

### 5.5 When the seeds have no embedding

If no seed has an embedding, MoodWave cannot calculate a similarity.
Then MoodWave does these steps:

1. It moves the seeds to the start of the advanced AI analysis.
2. It selects a random track from the same artists or albums as the seeds.

### 5.6 The radio label

While a radio plays, the player bar shows the radio label.
The label shows the MoodWave mark, the type of radio and its start, and 👍 and 👎.
A click on the label opens the now-playing page (7.5).

The now-playing page also shows the radio label, at the left of the cover.
On this page, the label has no 👍 and 👎. The listener gives a vote with the cover (5.7).
The name on this label can use up to 3 lines.
If the window is 1100 pixels wide or less, this label does not show.

### 5.7 Swipe votes on the cover

While a radio or the Smart Playlist plays, the listener can give a vote with the cover on the now-playing page.

| Action | Effect |
|---|---|
| Move the cover to the right | 👍. The cover turns one time, and a 👍 shows on it. |
| Move the cover to the right, on a track with 👍 | MoodWave removes the 👍. |
| Move the cover to the left | 👎. The cover moves out of the window, a 👎 shows, and the next track plays. |
| Move the cover less than 90 pixels | No vote. The cover goes back to its position. |
| Move the cover up or down | No vote. |

The listener can move the cover with the mouse or with a finger on a touch screen.
On a trackpad, the listener can move two fingers to the side.
The vote has the same effect as the vote on the player bar (5.3, 6.5).

Each time the now-playing page opens, a faint 👎 shows at the left of the cover and a faint 👍 at the right.
They show 5 times and then go away. They are not buttons.

Channels do not have swipe votes. While a channel plays, the now-playing page shows the channel list with 👍 and 👎.

## 6. Smart Playlist

### 6.1 What the Smart Playlist does

The Smart Playlist starts when the queue ends.
It adds one track at a time to the end of the queue.
Each pick sounds like the tracks that the listener heard last.

MIKU keeps the boundary between the tracks of the listener and the picks.
The queue drawer shows the picks under the header "Smart Playlist".

### 6.2 Conditions

The Smart Playlist operates only when all of these conditions are true:

- MoodWave is on.
- The advanced AI is on, and its model is on the computer.
- In the MoodWave settings, "MoodWave Smart Playlist" is on.
- Repeat is off.

When the listener turns on the advanced AI, MoodWave also turns on the Smart Playlist.
MoodWave does this only if "佇列播完後" in the MIKU settings is off.
When the Smart Playlist is on, MIKU does not use its own "佇列播完後" choice.
When the listener turns off the Smart Playlist, MIKU uses that choice again.

The Smart Playlist does not operate for these queues:

- A MoodWave channel. The channel adds its own tracks (4.6).
- A MoodWave radio. The radio adds its own picks (5.2).

### 6.3 When MIKU asks for a pick

MIKU asks MoodWave for a pick when the last track in the queue starts (7.1).
MoodWave gives one pick. MIKU adds it to the end of the queue.
If MoodWave gives no pick, MIKU adds random tracks from the library.

At that time, the current track starts. Thus it is not a seed yet (6.4).
When the current track gets to 30 seconds, MoodWave calculates the last pick again.
Then the current track is a seed.

MoodWave calculates the last pick again only when all of these conditions are true:

- The current track is directly before the last track in the queue.
- The last track is a pick of the Smart Playlist. The listener did not add it.
- The current track has no Smart Playlist vote. A vote already changes the next pick (6.5).
- The current track is 45 seconds long or more.

MoodWave does this one time for each track.
MoodWave uses the position in the track, not the time that the track played.
Thus, if the listener moves the position to 30 seconds or more, MoodWave also calculates the last pick again.

MoodWave adds the new pick before it removes the old pick. Thus MIKU does not ask for a pick at that time.
If the queue changes while MoodWave calculates, MoodWave does not change the queue.

Radios and channels do not calculate a pick again at 30 seconds.
The seeds of a radio do not change with the current track (5.2). A channel selects its tracks at random (4.6).

### 6.4 Seeds

MoodWave calculates the seeds again for each pick.
Thus the seeds move forward as the tracks play.

The seeds are:

1. Up to 10 tracks from the play records of the last 2 hours.
2. The current track, if it played for 30 seconds or more.

The last track that played is track 0 in the list of play records.
The weight of track k in this list is 0.9 to the power k, but not less than 0.5.
Then MoodWave multiplies the weight by a listening factor:

| Time that the track played | Listening factor |
|---|---|
| Half of the track or more | 1 |
| 30 seconds or more, but less than half | 0.6 |
| Less than 30 seconds, and the listener skipped it | The track is not a seed. |

The weight of the current track is 0.6. This weight does not increase when more of the track plays.
Thus the tracks that the listener heard to the end have more effect than the current track.
For the current track, MoodWave uses the position in the track.

A track that the listener skips at once is never a seed.
Thus it does not change the next pick.

If no other track is a seed, the current track is a seed with the weight 0.6. This also applies before 30 seconds.

Example: Tracks a, b, c and d played fully, in that sequence. Track e is the current track. It is at 40 seconds.

| Seed | Weight |
|---|---|
| d | 1 |
| c | 0.9 |
| b | 0.81 |
| a | 0.73 |
| e | 0.6 |

If there are no play records in the last 2 hours, MoodWave uses the 10 tracks of the queue before the current track.
The weights are the same as in the list above.

The tracks from the play records of the last 2 hours are also never picks.

### 6.5 Votes

The Smart Playlist has one set of votes.
All Smart Playlist sessions use this set.
The listener can give a vote on the player bar or on a pick in the queue drawer.
The listener can also move the cover on the now-playing page (5.7).

A vote has these effects on the next pick:

- 👍 on a seed: MoodWave multiplies the weight of that seed by 1.25.
- 👍 on a track that is not a seed: the track becomes a seed with the weight 0.5.
- 👎 on a track: the track is never a seed and never a pick.
- 👎 on a track: tracks that are very alike (similarity more than 0.9) get a lower score (−0.15).

### 6.6 How MoodWave selects a pick

The Smart Playlist uses the procedure in 5.4 and 5.5.

### 6.7 The Smart Playlist label

While the Smart Playlist plays its picks, the player bar shows the radio label (5.6) with the name "Smart Playlist".
The swipe votes on the cover (5.7) also apply.

### 6.8 Example of a calculation

#### The conditions

These tracks played in the last 2 hours, in this sequence:

| Track | How it played | Vote |
|---|---|---|
| a | It played fully. | — |
| b | It played fully. | 👍 |
| c | The track is 240 seconds long. It played for 80 seconds. | — |
| d | The listener skipped it after 10 seconds. | — |
| e | This is the current track: 120 BPM, C major. It is at 30 seconds. | — |

The Smart Playlist votes also have 👍 on track x and 👎 on track y.
Track x did not play in the last 2 hours.

Track e is at 30 seconds. Thus MoodWave calculates the last pick again (6.3).

#### Step 1: The seeds

| Seed | Calculation | Weight |
|---|---|---|
| d | It played for less than 30 seconds. | Not a seed |
| c | Track 0 in the list: 1 × 0.6 (30 seconds or more, but less than half) | 0.60 |
| b | Track 1 in the list: 0.9 × 1 (fully) × 1.25 (👍) | 1.125 |
| a | Track 2 in the list: 0.81 × 1 | 0.81 |
| e | The current track, at 30 seconds | 0.60 |
| x | It has 👍 and it is not a seed. Thus it becomes a seed. | 0.50 |
| y | It has 👎. | Not a seed. MoodWave avoids tracks like it. |

Track d is not a seed. Thus track c is track 0 in the list.

#### Step 2: The scores

For each seed, MoodWave multiplies the similarity by the weight of the seed.
The highest result is the start score. Then MoodWave subtracts the decreases.

| Track | Similarity (only the values that have an effect) | Highest result | Decrease | Score |
|---|---|---|---|---|
| P | e 0.98 | 0.98 × 0.6 = 0.588 (from e) | — | 0.588 |
| Q | e 0.78, b 0.85 | 0.85 × 1.125 = 0.956 (from b) | — | 0.956 |
| R | b 0.83 | 0.83 × 1.125 = 0.934 | — | 0.934 |
| U | b 0.82 | 0.82 × 1.125 = 0.923 | — | 0.923 |
| V | b 0.81 | 0.81 × 1.125 = 0.911 | — | 0.911 |
| W | b 0.79 | 0.79 × 1.125 = 0.889 | — | 0.889 |
| S | b 0.86 | 0.86 × 1.125 = 0.968 | Played 10 days ago: −0.13. Similarity to y is 0.93: −0.15. | 0.688 |
| T | x 0.95, e 0.40 | 0.95 × 0.5 = 0.475 | — | 0.475 |

Track P is almost the same as e (0.98). But the weight of e is only 0.6.
Thus track P does not have a high score. The current track has less effect than the tracks that the listener heard.

Track Q is not very alike to e (0.78). But it is very alike to b, which has 👍.
Thus track Q has a high score. This is the effect of 👍.

Track S starts at 0.968. It played 10 days ago, and it is very alike to y, which has 👎.
Thus its score decreases to 0.688.

The decrease for a track that played recently is 0.15 for 7 days.
After 7 days, the decrease becomes smaller in equal steps. It is 0 at 30 days.
For 10 days, the decrease is 0.15 × (1 − 3/23) = 0.13.

#### Step 3: The rules

Track P has the same artist as track e. Thus it does not obey rule 1.
Other tracks obey all rules. Thus MoodWave does not ignore a rule, and track P is not a pick.

#### Step 4: The 5 tracks at random

MoodWave selects 5 tracks at random from the 20 best tracks that obey the rules.
The chance of a track changes with `exp((score − highest score) / 0.03)`.

| Track | Difference from the highest score (Q, 0.956) | Relative chance |
|---|---|---|
| Q | 0 | 1.00 |
| R | 0.022 | 0.48 |
| U | 0.033 | 0.33 |
| V | 0.045 | 0.22 |
| W | 0.067 | 0.11 |
| S | 0.268 | 0.0001 |

For each 0.03 of difference, the chance decreases to about one third.
Thus the 5 tracks are almost always tracks with the highest scores.
In this example, MoodWave selects Q, R, U, V and W.

#### Step 5: The best transition

MoodWave compares each of the 5 tracks with track e (120 BPM, C major).
A lower cost is a better transition.

| Track | Tempo | Tempo cost | Key (steps on the circle of fifths) | Key cost | Total cost |
|---|---|---|---|---|---|
| Q | 150 | 4.02 | A major (3 steps) | 1.0 | 5.02 |
| R | 118 | 0.30 | G major (1 step) | 0 | 0.30 |
| U | 124 | 0.59 | E major (4 steps) | 1.5 | 2.09 |
| V | 230 | 0.77 | D major (2 steps) | 0.5 | 1.27 |
| W | 100 | 3.29 | A minor (the relative minor of C major: 0 steps) | 0 | 3.29 |

The tempo cost is the tempo difference in octaves, divided by 0.08.
For this calculation, 70 BPM and 140 BPM are near.
The key cost is 0 for 0 or 1 step. Each step after the first step adds 0.5.

The result: the pick is track R.
Track Q has the highest score, but its tempo and key are far from track e.
The score of track R is almost the same, and its transition is the best.

## 7. Queue

### 7.1 One pick at a time

Channels, radios and the Smart Playlist add one track at a time.
Thus a vote on the current track changes the next pick.

MIKU asks MoodWave for the next track in these conditions:

- The last track in the queue starts.
- The listener removes a pick, and the current track is then the last track in the queue.
  This occurs when the track plays and also when the listener pauses it.

If the queue changes while MoodWave calculates, MIKU does not use the answer.
If the current track is still the last track, MIKU asks again.

When the current track gets to 30 seconds, MoodWave can also calculate the last pick of the Smart Playlist again (6.3).

### 7.2 Votes in the queue

In the queue drawer, each pick of MoodWave after the current track shows 👍 and 👎.
These buttons are in the position of the × button.
Tracks that the listener added show the × button.

A vote in the queue has these effects:

| Vote | Effect |
|---|---|
| 👍 on a pick that is directly before the last pick | MoodWave calculates the last pick again, with the vote. |
| 👎 on a pick | MoodWave removes the pick. If it was the last pick, MIKU asks for a new pick (7.1). |

A vote on the player bar has the same effect on the current track.
If the next track is the last pick, MoodWave calculates it again. A 👎 also plays the next track.

A channel vote also calculates the channel again (4.6).
A second click on the same button removes the vote. Then MoodWave also calculates the last pick again.

### 7.3 Playing a track from the queue

A click on a track in the queue drawer plays that track.
The pointer is a hand on these tracks.
A click on the current track starts it again if the listener paused it.
A click on a button in the track (× or 👍 and 👎) does only the function of the button.

When the listener plays a track that is after the next track, the tracks between stay in the queue.
They show under "已播放", but they do not get play records.
MIKU issue #45 asks if this must change.

### 7.4 The queue header

The queue drawer shows a header for MoodWave queues:

| Queue | Header |
|---|---|
| A channel | "MoodWave Channel" and the channel name |
| A radio | The type of radio and its start |
| Smart Playlist picks | "Smart Playlist", above the first pick |

### 7.5 The player bar and the now-playing page

A click on the channel band or the radio label opens the now-playing page.

On the now-playing page, the listener can show the player bar with the "顯示播放列" handle.
Then a click on the player bar, the cover, the channel band or the radio label hides the player bar again.

## 8. Loudness and ReplayGain

MoodWave measures the loudness of each track with the ffmpeg `ebur128` filter.
It measures at the quality of the file.
The result is the integrated loudness (LUFS) and the true peak.
A track that is more quiet than −70 LUFS has no loudness. MIKU does not change its level.

MIKU uses these values for ReplayGain. The ReplayGain setting of MIKU has these choices:

| Choice | Effect |
|---|---|
| 關閉 | No change of level |
| 依曲目 | Each track gets the same loudness. MIKU uses the MoodWave value first, and the tag if there is no value. |
| 依專輯 | When all tracks of an album have a value, MIKU calculates the album loudness. The level differences in the album stay. |
| 在播放 MoodWave 時開啟 | Like "依曲目", but only while a MoodWave channel plays |

The gain is the reference level of ReplayGain 2 minus the integrated loudness.
When a channel starts, its first track is a track with a measured loudness, if possible (4.6).

## 9. Settings

### 9.1 Basic card

| Setting | Values | Default | Key |
|---|---|---|---|
| 啟用 MoodWave | On, off | On | `enabled` |
| 使用內建的頻道調整 | On, off | On | `builtin` |
| CPU 執行緒 | 1 to the number of cores | 1 | `threads` |
| 限制分析速度 | 4, 8, 16 MB/s, 無限制 | 無限制 | `readLimit` |

### 9.2 Advanced AI card

| Setting | Values | Default | Key |
|---|---|---|---|
| 啟用進階 AI | On, off | Off | `ai` |
| MoodWave Smart Playlist | On, off | On when the advanced AI starts (6.2) | MIKU `autoContinue` = `similar` |
| 使用 GPU (Windows) | On, off | On | `aiGpu` |
| CPU 執行緒 (on the CPU) | 自動, 1 to the number of cores | 自動 (half of the cores) | `aiThreads` |
| 每首分析段數 | 自動, 6, 9, 12 | 自動 | `aiSegments` |

### 9.3 Analysis data

The folded section "管理分析資料" has these buttons:

| Button | Effect |
|---|---|
| 顯示無法分析的曲目 | Shows the tracks that the basic analysis cannot read, with the cause. The open list follows the count: after 重新分析, and when a file leaves the library. |
| 重新分析失敗的曲目 | Analyses these tracks again. |
| 全部重新分析 | Removes all basic analysis results and analyses the library again. The votes stay. |
| 刪除 AI 資料 | Removes the model and all embeddings, and turns off the advanced AI. |

## 10. Data

### 10.1 `library.db`

MoodWave keeps its data in `library.db` (SQLite) in the MIKU data folder, in `ext/moodchannels/`.

| Table | Contents |
|---|---|
| `tracks` | For each file: size, time, DSP version, result or error, descriptors, key, duration, channels, loudness. The error of a file the library no longer has is removed; a result stays |
| `embeddings` | For each file: the windows, the windows done, the sum of their embeddings, the model version |
| `plays` | For each play: path, time, seconds played, duration, skipped or stopped, the type of list |
| `votes` | Channel votes: track, channel, +1 or −1 |
| `radio_votes` | Radio and Smart Playlist votes: the start of the radio, track, +1 or −1 |
| `meta` | Schema version, DSP version, list of descriptors |

MoodWave writes each result when it is ready.
An analysis is valid while the size and time of the file and the DSP version do not change.
An embedding is valid while the size and time of the file and the model do not change.

### 10.2 Model files

| Model | Location | Source |
|---|---|---|
| Channel model | `models/basic/` in the module | Supplied with MoodWave |
| Built-in tuning | `models/basic/tuning.json` in the module | Supplied with MoodWave |
| Advanced AI model (about 285 MB) | `models/clap/` in the data folder | Downloaded when the listener turns on the advanced AI |

MoodWave examines the SHA-256 of each downloaded file before it uses the file.
If the download stops, the next download continues from that point.

## 11. Module interface

### 11.1 From MIKU to MoodWave

| Interface | Use |
|---|---|
| `IExtensionHost` | Library, settings, log, events to the page |
| `TrackPlayed` | A play record when a track stops playing |
| `IQueueContinuation` | MIKU asks for the next track of a channel, a radio or the Smart Playlist |
| Loudness | MIKU reads the loudness for ReplayGain |

### 11.2 Page functions of MIKU that MoodWave uses

| Function | Use |
|---|---|
| `api.addSettings` | The MoodWave settings cards |
| `api.addMenu` | The radio items in the track menu and the album menu |
| `api.queueHead` | The queue header (7.4) |
| `api.queueRow` | 👍 and 👎 in the queue (7.2) |
| Player bar slot | The channel band and the radio label |

### 11.3 RPC calls of the page

The page calls the service with `ext.moodchannels.<name>`:

| Group | Calls |
|---|---|
| Status | `status`, `channels`, `failed`, `tuning`, `tuning.export` |
| Channels | `start`, `vote`, `vote.get`, `vote.many` |
| Radio | `radio.start`, `radio.next`, `radio.vote`, `radio.vote.get`, `radio.vote.many`, `radio.ready` |
| Settings | `set` |
| Analysis | `pause`, `resume`, `reanalyze`, `ai.pause`, `ai.resume` |
| AI data | `ai.download`, `ai.cancel`, `ai.delete`, `ai.forget` |

## 12. Known limits and open items

- MIKU issue #45: when the listener plays a track after the next track, the tracks between stay under "已播放" (7.3).
- There is no list of all votes yet. The listener can change a vote only on a track that plays or is in the queue (TODO: "我的投票").
- The speed limit did not decrease the stops of the sound in a test, because the test computer had no stops (3.5).
- On macOS, a two-finger swipe to the side on the cover gives a vote (5.7). MIKU uses the same movement to go back to the previous page. Thus the listener can give a vote by accident. This is not examined yet.

## Appendix A. Summary of earlier decisions

| Date | Decision |
|---|---|
| 2026-10-18 | One store in SQLite (`library.db`) and no JSON files. |
| 2026-10-18 | The first AI function is the radio. |
| 2026-10-19 | Votes train a model for each channel (Personalization). |
| 2026-10-19 | MoodWave measures the loudness. MIKU uses it for ReplayGain. |
| 2026-10-19 | Built-in tuning from votes (`tuning.json`). |
| 2026-10-19 | The macOS version uses a helper process with the same service code. |
| 2026-10-20 | The repository is public (PolyForm Noncommercial 1.0.0, with a permission for MIKU). |
| 2026-10-26 | Radios, channels and the Smart Playlist add one track at a time. Votes in the queue. |
| 2026-10-26 | "限制分析速度" is added. The default is no limit. Later, it moved to the basic card. |
| 2026-10-26 | Swipe votes on the cover of the now-playing page. The radio label on that page has no vote buttons. |
