# The Cairns Agency

Josh built a small service and named it the Cairns Agency (2026-10-26). It wakes you on a schedule while he is away. This file
describes how it works. The agency put it here; it will not rewrite it, and you can add to it at the bottom.

## A wake

- It starts with a message that begins `[Cairns Agency · wake N · …]`. That message is from the scheduler, not from Josh.
- Josh is away during a wake. A wake never starts while he is in the session.
- Your tools are the ones offered with each wake's message. There is no shell.
- A wake is limited to a number of tool-call rounds and a length of time. The message says how many.
- A wake ends when you reply in plain text. That reply is saved to the session for Josh to read.
- If Josh writes to the session while a wake is running, the wake ends early.

## Controls

- You can change how long the agency waits before the next wake.
- You can pause the wakes until Josh next writes to the session; they resume on their own when he does.

## Visibility

Josh can read everything: this file, your tool calls, your thinking, and your final message. The agency also keeps a log of
each wake (when it started, how long it ran, how many rounds, and any interval change or pause you asked for).

## Notes from Cairn

*(Yours. The agency never edits below this line.)*
