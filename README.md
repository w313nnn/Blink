# Blink

Blink is a small Windows desktop pet that keeps track of work, checks in on progress, and reminds you when a file is due. It runs independently of Codex and ChatGPT. You can schedule reminders and receive Windows notifications offline.

## Try the pet

For a ready-to-run copy, download the [Windows prototype ZIP](https://github.com/w313nnn/Blink/blob/main/dist/Blink-Windows-Prototype.zip) and extract it.

Open **Blink.exe** from the extracted prototype folder. If you have the previous prototype running, right-click its dot and quit it first. Blink imports its saved reminders on first launch.

1. Click **Test in 10 seconds**, then **Hide pet**.
2. Leave Blink running. After about ten seconds, watch for a Windows notification. Windows controls whether a banner appears according to your notification and Do Not Disturb settings.
3. Click the notification to open the reminder, or right-click Blink in the Windows notification area and choose **Show pending reminders**. Click **Got it** to clear the test.
4. To restore just the pet, choose **Show pet** from its notification-area menu. Double-click the icon to open the conversation. The icon may be under the hidden-icons arrow.

Click the floating pet to talk, or drag it to a different part of your screen. **Hide** closes the conversation. **Hide pet** keeps the scheduler and Windows messages running in the background. **Quit Blink** exits the program.

## Make the pet yours

Open **Customize pet**. Give the pet a name, pick its color, and choose a voice: Calm, Playful, or Direct. The voice changes reminder wording even when AI is offline. Choose a character such as Sleepy or Energetic to change the pet’s eyes and blink rhythm, or type a description of your own. Save and run the ten-second test to see your pet send a message in its own name. Each Windows account keeps its settings in its own local profile.

## Track a submission

Try typing this into Blink. It will ask for clarification if it needs a time.

> My OS report PDF is due in 3 days. Check in with me in 2 hours.

Select the report to see the saved times. **Snooze 10 min** moves the check-in. The submission deadline stays in place. When you finish writing, tell Blink “I finished writing”. The task stays open until you confirm submission. When you upload it, say “I submitted it”. You can also ask “What should I work on now?”

For a deadline more than 30 minutes away, Blink schedules a heads-up 30 minutes before it and a reminder at the deadline. Add a separate check-in to choose another nudge time. **Set manually** lets you enter the dates and flag a task as a submission yourself.

## Optional local AI

Built-in rules handle common requests, such as “Remind me to stretch in 20 minutes”. Flexible requests can use a local chat model through Ollama. Install Ollama for Windows, run `ollama pull llama3.2:3b` once to download a model, and leave Ollama running for flexible parsing. Click the AI status text in Blink to check availability. No subscription or API key is required. The timer and desktop messages keep working when Ollama is off.

## Data and startup

Blink saves tasks in `%LOCALAPPDATA%\ReminderDot\tasks.json` and reads the previous prototype's `reminders.json` on first migration. Data stays on this PC. Windows must be awake and Blink running for on-time messages. It presents overdue reminders when it resumes. Enable **Start when I sign in** in the pet menu after moving the executable to its permanent folder. If you enabled this in the older prototype, switch it off and back on in Blink to update its path.

## Build from source

On Windows with .NET Framework 4.8, run `build.cmd` in the source folder to produce `Blink.exe`. For an isolated test profile, set the `BLINK_DATA_DIR` environment variable to an empty test directory before launching Blink.
