# Reminder Dot

Reminder Dot runs independently of Codex and ChatGPT. Scheduling and desktop alerts work offline without an account, subscription, or API key. For optional AI parsing, install a local chat model with Ollama, for example with `ollama pull llama3.2:3b`, and leave Ollama running while interpreting a request. Once you save a reminder, only Reminder Dot needs to stay running.

A standalone Windows desktop companion. Double-click `build.cmd` to build with the .NET Framework compiler included on Windows systems with .NET Framework 4.8, then open `ReminderDot.exe`.

1. Type “Remind me to stretch in 20 minutes” and click **Read time from text**, or write a message and choose its date and time.
2. Review the message and time, then click **Set reminder**.
3. Close the editor. The teal dot stays on your desktop and displays a message with a sound when a reminder is due. Click **OK** to acknowledge it.

Click the dot to manage reminders, drag to move it, or right-click for **Start when I sign in** and **Quit**. Enable sign-in startup after placing the executable in its permanent location.

Relative times in seconds, minutes, hours, and days work offline. For phrases such as “call Maya next Friday at noon”, the app can query a running Ollama instance on the local machine. Install Ollama and pull a local chat model to enable this option. The app submits the text only when you click **Read time from text** and the built-in parser needs help. Always check the proposed time before saving.

Reminders are stored in `%LOCALAPPDATA%\ReminderDot\reminders.json`. Windows must be awake and the app running to show a message at its scheduled time. Overdue reminders appear when you wake the computer or reopen the app.
