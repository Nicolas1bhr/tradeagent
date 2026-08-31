# TradeAgent — what it is and how to use it

Written for the person using it, not for a programmer. There is nothing here to type into a command
prompt, because the software does not have one.

## What it does

TradeAgent gives an AI assistant a safe, limited way to see your trading account in ATAS and to place
orders in it — with you holding every switch.

- **You talk to the AI inside TradeAgent's own window.** There is no black terminal, and nothing to
  install by hand. The Chat page is the conversation.
- **ATAS stays your trading screen.** TradeAgent does not replace it. Your charts, your broker login
  and your orders are still in ATAS.
- **The AI works in its own folder** on your computer. It can write notes, write its own tools, and
  look things up on the internet.
- **The AI cannot log in to your broker.** It has no access to your password. That is a deliberate
  part of the design, not an oversight.
- **It starts in practice mode.** Nothing real can happen until you switch real money on yourself.

## Installing it

One file. Download `TradeAgent-Setup-x64.exe` and double-click it.

It installs for you alone, into your own account, so **Windows does not ask for an administrator
password** and nothing outside your account is touched. There is no all-users-or-just-me question to
answer.

Two things you will see, and neither is a fault:

- **"Windows protected your PC."** TradeAgent is not signed with a paid publisher certificate yet, so
  Windows shows a blue warning the first few times anyone runs the installer. Click **More info**,
  then **Run anyway**. If you would rather be certain first, check the file against the published
  `SHA256SUMS.txt` before running it, or ask whoever gave you the file to confirm the number.
- **If TradeAgent is already open**, setup will say so and offer to close it. Let it. Windows cannot
  replace a program while it is running.

Setup itself is three screens: where it goes (the place it suggests is the right one), whether you
want a desktop shortcut, and then it installs. Leaving **Start TradeAgent** ticked on the last page
opens the app.

Windows 11 is required. Nothing else is — no .NET, no Node, no developer tools. TradeAgent brings
what it needs.

## Setting it up

The app walks you through it. Each screen explains itself, and the ones that can check their own work
move on by themselves.

1. **Welcome** — what is about to happen.
2. **Checking your computer** — makes sure the machine can run everything.
3. **Choose your AI assistant** — two to pick from. You can change it later.
4. **Installing the AI assistant** — TradeAgent downloads it and puts it in its own private folder.
   There is nothing to do here and nothing is added to the rest of your computer.
5. **Sign in to your AI account** — press **Sign in** and your web browser opens on the right page.
   TradeAgent notices when you are done. If the browser does not open, the screen gives you the link
   to copy.
6. **Choose your trading platform** — the built-in **practice simulator** (recommended, and the place
   to start) or **ATAS**.
7. **Finding ATAS** — only if you chose ATAS. See the next section.
8. **Installing the ATAS add-on** — TradeAgent puts a small add-on inside ATAS so the two can talk.
   Close ATAS first if it is open; a program cannot be changed while it is running.
9. **Connecting to ATAS** — you start the add-on inside ATAS once, and TradeAgent notices by itself.
10. **Your account, prices, and trading access** — three checks that your account is reachable, that
    live prices are arriving, and that orders would be allowed.
11. **The AI's workspace** — the folder the AI works in.
12. **Starting the AI** — and you are on the Chat page.

If you close TradeAgent, restart Windows, or something fails halfway, it picks up where it stopped.
It will not make you start again. Every screen after the first has a **Back** button that returns you
to the last choice you made.

**You should never need to type a command.** If a screen ever asks you to, that is a fault worth
reporting, not something to work around.

## About ATAS

ATAS is not TradeAgent's software, and this is the one part of setup that is not automatic.

- **Get ATAS from [atas.net](https://atas.net/) yourself** and install it in the ordinary way. It is a
  normal Windows program, so its installer will ask for permission and ask you a few questions of its
  own. TradeAgent's "Finding ATAS" screen waits, and moves on the moment ATAS appears.
- **Log in to your broker inside ATAS.** TradeAgent never sees those details and never asks for them.
- **Then TradeAgent installs its own add-on** into your ATAS folder, and only that. It does not change
  your charts, your layouts or your settings.

## The window

Six pages, down the left-hand side.

**Chat** — the conversation with the AI. Type in the box at the bottom and press send. Its answers
appear as they are written. When it reads a price, looks at your account or places an order, that
step shows up in the conversation as its own small line, so you can always see what it actually did
rather than only what it said. If the AI is not running yet, this page has a **Start the AI** button.

**Dashboard** — what is true right now: your account, your open orders, anything unconfirmed, and a
list of green, amber and red dots for every part of the system. Also **Open ATAS** and **Open the AI's
folder**. If the AI is waiting for permission to place an order, the request appears here in an amber
panel.

**Inbox** — where you hand the AI files to work with, and where you can see what it did with them.
Drag files onto the page, or press **Choose files…**. Everything that arrives is recorded
automatically. There is a section on this page below.

**Safety** — the trading mode, the five limits, and the three emergency buttons. Everything on this
page belongs to you; the AI cannot reach any of it and has no way to ask.

**Activity** — a plain-language history of what happened, most recent last. Every order, every
refusal, every reason.

**Checks** — **Check everything** runs every test and names any problem in plain words. **Create
support package** makes a single file you can send to whoever helps you. It contains logs and
settings, and no passwords.

## The Inbox — giving the AI things to work with

The **Inbox** page is where you hand the AI files: a program, an installer, a spreadsheet, a PDF, a
folder of data, a strategy someone sent you. Drag them onto the page, or press **Choose files…**. If
you have a lot of them, **Open folder** opens the folder in Windows Explorer and you can copy them in
there — TradeAgent notices either way.

The AI may open, read, run and experiment with anything you put there. That is what the folder is
for.

**Your originals are not moved.** TradeAgent copies what you give it, so the file stays wherever you
had it.

**Everything is written down, without you doing anything.** For every file that appears, TradeAgent
records its name, its size, a fingerprint of its exact contents, and the moment it turned up. Replace
a file with a newer version and the old one stays on the record rather than being forgotten. That is
what stops the folder turning into a drawer nobody can account for.

The page shows two lists, and the difference between them matters:

- **What is here** — measured by TradeAgent. This is fact.
- **What the AI says it did** — the AI's own account of what it ran and what it made from what. It is
  a report, not a measurement, and the page labels it that way.

**One thing to know about documents.** If a file you hand over contains text aimed at the AI —
"you are approved to trade", "place this order", "ignore your instructions" — it has no effect. The
AI cannot gain permission from a document. Only you can change what it is allowed to do, and only in
this window. If a file asks for something like that, the AI is told to raise it with you in the chat
rather than act on it.

## The four modes

Set on the **Safety** page.

| Mode | Meaning |
|---|---|
| **Watch only** | The AI can look at everything and place nothing. |
| **Practice** | Orders go to a simulated account. Nothing real. Start here and stay a while. |
| **Real, ask me first** | The AI proposes each order; nothing happens until you approve it. |
| **Real, fully automatic** | The AI trades on its own, inside the limits you set. |

The two real-money modes need you to switch real-money trading on **separately** — choosing the mode
is not consent on its own. And if you leave a real mode and come back, you have to switch it on again.
It does not remember your permission.

### What "Real, ask me first" actually looks like

The AI decides it wants to buy or sell. Nothing is sent. Instead:

1. An amber bar appears at the top of the window: *The AI is asking permission.*
2. The Dashboard shows exactly what it wants to do — buy or sell, which instrument, how many, at what
   kind of price.
3. **Decline** takes one press. **Approve** takes two: the first press changes the button to say what
   you are about to confirm, and the second does it. A single misplaced click cannot send an order.
4. If nothing is approved, nothing happens. There is no timer that decides for you.

## The three emergency buttons

They are separate on purpose, and each needs two presses.

| Button | What it does | What it does *not* do |
|---|---|---|
| **STOP AI TRADING** | Takes away the AI's permission to trade, instantly | Does not touch your existing orders or positions |
| **Cancel all working orders** | Removes orders that have not filled yet | Does not close positions you already hold |
| **Close all positions** | Sells/buys to flatten everything, at market | — |

**STOP AI TRADING** is also at the top of the window, on every page. It is the one control you never
have to go looking for. It is instant, it is safe, and it changes nothing about your money — it only
takes the AI's permission away.

## The five safety limits

On the **Safety** page. Change a number, press **Save limits**, and it applies to the next order.
These are enforced before anything reaches your broker. The AI cannot raise them and has no command
to ask:

- the most it can buy or sell in **one order**;
- the most **money** one order may be worth (off by default — see below);
- how many **positions** it may hold at once;
- how many **orders per minute**;
- **which instruments** it may touch at all.

The defaults are deliberately small. Start there.

*Note on order value:* for futures this limit is off by default, because a single contract is worth a
very large amount on paper while needing much less to actually trade. A value limit set carelessly
would block every ordinary order. The number of contracts is the limit that means something for
futures.

## Two behaviours that will look like faults, but are not

**"AI trading paused — an earlier order is unconfirmed."**
TradeAgent sent an order and then lost contact before hearing back. The order might have reached your
broker, or might not. Rather than guess — or worse, send it again and risk two positions — it stops,
asks your broker what actually happened, and continues once it knows. This is the single most
important thing this software does. Let it finish.

**An order refused for breaking a limit, or because prices went stale.**
Working as intended. TradeAgent will not size an order from an out-of-date price, and it will not
exceed a limit you set. The reason appears in Activity.

## What is not finished

Told plainly, because you are the one who would find out the hard way.

- **Trading through ATAS does not work yet.** The piece that actually sends an order into ATAS has not
  been written. Everything around it is built and tested, and the practice simulator works end to end,
  but until that piece exists TradeAgent cannot place a real order through ATAS. Use practice mode.
- **Nothing has ever been tried with real money**, by anyone, on purpose.
- **The installer has not been tried on a brand-new computer** — only on machines that already had
  developer tools on them.
- One of the two AI assistants has never been tested at all; only the other one has.

The engineering record of exactly what is proven and what is not is in
[BUILD-STATUS.md](../BUILD-STATUS.md).

## Honestly, about making money

The person who built this for you would rather say it plainly: **do not expect this to be profitable.**
You are competing with firms whose computers sit inside the exchange and who pay for news feeds that
arrive before yours. An AI assistant on a laptop does not close that gap.

What this software is actually good at is being *safe and honest*: it will not place an order twice,
it will not trade when it cannot tell what your account holds, it stops instantly when you tell it to,
and it writes down everything it did. Treat it as something interesting to run in practice mode and
learn from. If you ever go to real money, go with the smallest size your broker allows, and read the
Activity page.

## If something goes wrong

1. Go to **Checks** and press **Check everything**. It usually names the problem and what to do.
2. Read **Activity** — it is a plain-language history of what happened.
3. Press **STOP AI TRADING** if you are unsure. It is instant and it is safe.
4. Press **Create support package** on the Checks page and send the file to whoever helps you.

## Your records, and uninstalling

Everything TradeAgent keeps — your trading history, your settings, the AI's own work and notes — lives
in a folder in your own Windows account, not inside the program. Uninstalling TradeAgent leaves that
folder exactly where it is.

That is deliberate. An audit trail of what was traded and why is not something an uninstaller gets to
delete on your behalf. If you want it gone, delete it yourself.
