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

One file. Download `TradeAgent-Setup-x64.exe` from the
[releases page](https://github.com/Nicolas1bhr/tradeagent/releases/latest) and double-click it.

It installs for you alone, into your own account, so **Windows does not ask for an administrator
password** and nothing outside your account is touched. There is no all-users-or-just-me question to
answer.

Two things you will see, and neither is a fault:

- **"Windows protected your PC."** TradeAgent is not signed with a publisher certificate — that was
  a deliberate decision to leave for later — so Windows may show a blue warning the first time
  anyone runs the installer it has just downloaded. The **Run anyway** button is hidden until you
  click **More info** first. So: **More info**, then **Run anyway**. Expect it; an unsigned
  installer is exactly what that warning is for. If you would rather be certain first, ask whoever
  gave you the file to confirm the checksum published beside it.
  *This particular screen has not been walked yet.* The one installation done from the internet on
  the test machine arrived without the mark Windows uses to decide, so no warning appeared and
  nobody has seen it in this product's own case.
- **If TradeAgent is already open**, setup will say so and offer to close it. Let it. Windows cannot
  replace a program while it is running.

Setup itself is three screens: where it goes (the place it suggests is the right one), whether you
want a desktop shortcut, and then it installs. Leaving **Start TradeAgent** ticked on the last page
opens the app.

Windows 11 is required. Nothing else is — no .NET, no Node, no developer tools. TradeAgent brings
what it needs.

### What you will be asked to do

Once, at most: **click Yes on a Windows permission prompt.** That is the whole of it.

TradeAgent installs into your own account only, so its own installer never asks for an
administrator password and there is no all-users-or-just-me question to answer. The one place a
prompt can appear is when TradeAgent installs **ATAS** for you, because ATAS is somebody else's
program and installs itself for the whole machine. Windows asks; you click Yes.

If you click No, nothing is installed and nothing is changed, and TradeAgent says so in those
words. You can try again whenever you like.

Nothing in TradeAgent will ever ask you to type a command, open a black window, or copy an
instruction into one. If a screen ever does, that is a fault worth reporting, not something to work
around.

## Keeping it up to date

TradeAgent looks for a newer version of itself when it starts, and once every six hours after that.
When there is one, a blue strip appears under the top bar: *TradeAgent 0.2.0 is available*, with
**What's new**, **Install update** and **Later**.

**Nothing is ever installed without you.** The strip is the whole of what happens on its own. Pressing
**Install update** asks a second time — the button changes to say exactly what the next press will do,
including anything it would interrupt, like *Confirm: close TradeAgent and install 0.2.0, 1 order
still working* — and only that second press starts anything.

What then happens, in order: the new version downloads, TradeAgent checks the file is exactly the one
that was published, TradeAgent closes, the update installs itself without asking you anything, and the
new version opens on its own. You will see a small progress window from the installer. There is no
black terminal at any point.

While an order's outcome is unconfirmed, **Install update** on the strip is simply switched off, and
the strip says *"TradeAgent 0.2.0 is available. It can be installed once the unconfirmed order is
settled."* rather than inviting a press it already knows will be refused.

**Later** puts the strip away until the next time you open TradeAgent. The offer does not disappear:
the **Settings** page always shows which version you are running, which is the newest published one,
and the same **Install update** button. If you would rather TradeAgent never went looking, **Turn off
automatic checks** on that page stops it — you can still press **Check for updates** yourself whenever
you like.

### When it refuses

TradeAgent would rather not update than update to something it cannot vouch for, and it says which
in a sentence you can read.

- **It cannot prove the file is the one that was published.** Every release is published with a
  checksum — a number computed from the file's exact contents. If that number is missing, cannot be
  fetched, or does not name the file being offered, **nothing is downloaded and nothing is
  installed**, and the reason appears on the blue strip and on the Settings page: *"TradeAgent 0.2.0
  cannot be verified… Nothing was installed."* The version you are running is untouched. There is no
  press that gets past this, and there should not be: the checksum is the whole of the proof.
- **The file changed between the download and the moment of installing.** Checked again immediately
  before the installer is started, and a mismatch stops it there.
- **An order's outcome is unconfirmed.** TradeAgent will not replace itself while it cannot account
  for one of your orders — that is the one moment an update could lose track of real money. It says
  so: *"TradeAgent will not replace itself while an order's outcome is still unconfirmed… Settle or
  reconcile it on the Dashboard, then install."* Settle it on the Dashboard, then press again.
  If TradeAgent cannot even tell whether anything is unconfirmed, it refuses for that reason too,
  and asks you to close and reopen it.

The refusal is asked twice: once before the download, so a refusal does not cost you a hundred
megabytes, and once again just before the installer runs, so the answer is true at the moment it is
acted on. And from the moment the download starts until the installer takes over, **TradeAgent stops
accepting new orders** — the other half of the same rule.

**Every press you make is written into your Activity history**, refusal and all, so you can find it
later — and so "did I press that again?" is a question with an answer. The automatic six-hourly check
is the one thing that does not repeat itself in the log: the same refusal, over and over, would fill
the page four times a day, so it is recorded the first time and again whenever the reason changes.

Three more things worth knowing:

- **An update never touches your records, your settings or ATAS.** It replaces the program only.
- **Your AI cannot update TradeAgent.** It cannot check, download or install anything — the same rule
  that keeps it away from the mode switch and the kill switch.
- **The check needs the internet, and that is all it needs it for.** If your machine is offline the
  Settings page says it could not check, and nothing else changes.

## Setting it up

The app walks you through it. **There are sixteen screens, and the counter at the top of each one says so —
STEP 4 OF 16.** Most of them you will never see. A screen that can check its own work does not ask
you to confirm you did it; it checks every two seconds and moves on the moment the answer is yes, so
it goes past in a blink. On the one full walk done so far, on Windows with ATAS, **eight of the
sixteen were shown and eight went past by themselves.** Which eight depends on your machine — a
screen appears only if it is waiting for something.

These are all sixteen, by the name each one shows at the top.

1. **Welcome** — what is about to happen.
2. **Checking your computer** — the machine can run everything. Passes by itself.
3. **Choose your AI assistant** — two to pick from. You can change it later.
4. **Installing the AI assistant** — TradeAgent downloads it into its own private folder. Nothing to
   do, and nothing is added to the rest of your computer. Passes by itself once it is there.
5. **Sign in to your AI account** — press **Sign in** and your web browser opens on the right page.
   TradeAgent notices when you are done. If the browser does not open, the screen gives you the link
   to copy. Passes by itself once you are signed in.
6. **Choose your trading platform** — the built-in **practice simulator** (recommended, and the place
   to start) or **ATAS**.
7. **Finding ATAS** — only if you chose ATAS. Passes by itself once ATAS is on the machine. See the
   next section.
8. **Installing the ATAS bridge** — TradeAgent puts a small piece of itself inside ATAS so the two
   can talk. Close ATAS first if it is open; the file cannot be placed while ATAS is using the
   folder. The button says **Install the add-on**.
9. **Connecting to ATAS** — five numbered steps to do inside ATAS once: open ATAS, open a chart, open
   **Strategies** for that chart, choose **TradeAgent Bridge**, press **Add**, then press **Start**.
   If it is not in the list, press the refresh button at the top of the strategy list — ATAS only
   rereads the folder when asked. You do not tell TradeAgent when you are done; it notices.
10. **Finding your trading connection** — passes by itself.
11. **Choose your account** — the one account the AI is allowed to see and trade. It will never touch
    another.
12. **Checking live prices** — passes by itself.
13. **Checking trading access** — passes by itself.
14. **Creating the AI's workspace** — the folder the AI works in.
15. **Starting the AI**.
16. **Setup complete** — and you are on the Chat page.

**It resumes.** Progress is written down as each screen is finished, so closing TradeAgent, restarting
Windows, or a failure halfway leaves you exactly where you stopped — nothing already done is walked
again. That was tried: closed at step 9 of 16, reopened, and it came back on step 9 with nothing
repeated.

**Back does not go back one screen; it goes back to your last real choice.** There are four of those
— **Welcome**, **Choose your AI assistant**, **Choose your trading platform** and **Starting the AI**
— and the button names the one it will take you to, so you can always read where you are about to
land. Going back re-walks everything after it. *Known rough edge:* **Choose your account** is a real
decision but is not one of the places Back can return you to.

**You should never need to type a command.** If a screen ever asks you to, that is a fault worth
reporting, not something to work around.

## About ATAS

ATAS is not TradeAgent's software, and this is the one part of setup that is not automatic.

- **Get ATAS from [atas.net](https://atas.net/) yourself** and install it in the ordinary way. It is a
  normal Windows program, so its installer will ask for permission and ask you a few questions of its
  own. TradeAgent's "Finding ATAS" screen waits, and moves on the moment ATAS appears.
- **Log in to your broker inside ATAS.** TradeAgent never sees those details and never asks for them.
- **Then TradeAgent installs its own bridge** into your ATAS folder, and only that. It does not change
  your charts, your layouts or your settings.
- **You start the bridge inside ATAS once**, on a chart, the way you would start any ATAS strategy.
  After that it comes back by itself whenever ATAS runs it.

**One thing to expect after an update.** The bridge and TradeAgent have to be the same generation. If
TradeAgent is updated and the piece inside ATAS is an older one, TradeAgent refuses to talk to it
rather than guess, and says exactly that on the Dashboard and on the Checks page: *"bridge 0.1.1
speaks protocol 2, this build speaks 3 — press Reinstall the bridge on the Checks page."* It is not a
fault, and it is not a password problem — TradeAgent tells those two apart on purpose, because they
look identical from the outside and have completely different repairs.

**Putting the bridge back.** That repair is a button, and it is where the sentence says it is. Open
**Checks**: whenever the bridge is missing, refused, or an older one than TradeAgent expects, a card
called **The ATAS bridge** appears there with **Reinstall the bridge** on it. The same card is always
on the **Settings** page, whether anything is wrong or not, for the times somebody has told you to put
the bridge back.

Press it once and it turns red and says what the second press will do; press it again and TradeAgent
replaces the bridge. Trading through ATAS stops until the bridge is started again — so close ATAS
first if it is open, then start **TradeAgent Bridge** on a chart the way you did during setup.
TradeAgent notices by itself that it is back; you do not have to tell it. If ATAS is open and holding
the file, TradeAgent says so and asks you to close ATAS and press the button again. Nothing else is
asked of you, and nothing is opened outside the TradeAgent window.

## The window

Seven pages, down the left-hand side.

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

**Settings** — which trading platform is in use, which account the AI may trade, and which version of
TradeAgent you are running. You can change the platform and the account here after setup. Changing
the platform closes the current connection and clears your account choice, because an account on one
platform does not exist on the other; it moves no money and cancels nothing.

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

1. An amber bar appears at the top of the window: *"The AI is asking permission — 1 order waiting"*,
   with **Review the request**.
2. The Dashboard shows exactly what it wants to do — buy or sell, which instrument, how many, at what
   kind of price.
3. **Decline** takes one press. **Approve** takes two: the first press changes the button to say what
   you are about to confirm — *"Confirm: place this order"* — and the second does it. A single
   misplaced click cannot send an order.
4. **A request goes stale after fifteen minutes.** The line under it says so: *"asked at 14:02 —
   approve before 14:17; from then, approving declines it instead."* Nothing is sent when the time
   runs out; the request simply stops being approvable, and pressing Approve after that declines it.
   A price that was worth acting on a quarter of an hour ago is not one to act on now.
5. **Approving is not a rubber stamp on an earlier decision.** Every rule is checked again at the
   moment you press — the mode, the kill switch, the account, your limits, the connection, the price.
   So an approval can be refused for a reason that did not exist when the AI asked, and the Dashboard
   tells you which one.

## The three emergency buttons

They are separate on purpose.

| Button | Presses | What it does | What it does *not* do |
|---|---|---|---|
| **STOP AI TRADING** | one | Takes away the AI's permission to trade, instantly | Does not touch your existing orders or positions |
| **Cancel all working orders** | two | Cancels the orders it found, one by one | Does not close positions you already hold |
| **Close all positions** | two | Sells/buys to flatten everything, at market | — |

**STOP AI TRADING** is one press, in both directions, and it is at the top of the window on every
page. It is the one control you never have to go looking for. It is instant, it is safe, and it
changes nothing about your money — it only takes the AI's permission away. It is one press because a
mis-press costs nothing and hesitating costs money. When it is on, the same button says **RESUME AI
TRADING**.

The other two move money, so they are two presses: the first press changes the button into the
sentence it is about to carry out — *"Confirm: close all positions with market orders"* — and only
the second does it.

### What the other two do afterwards, and why it looks like an alarm

**Both of them stop the AI from trading until you have read the result.** That is deliberate, and it
is the most surprising thing on this page, so it is worth understanding once.

Before either one touches your broker, TradeAgent writes down every order or position it is about to
act on — one line each. Then it sends the instructions. From that moment those lines count as work
whose outcome is unconfirmed, so trading pauses and each line appears on the Dashboard for you to
confirm. Each line says what TradeAgent asked for, what the platform answered, and — read fresh from
your account a moment later — what your position on that instrument is *now*. Those are two different
facts, and a close that reported "filled" over a position that is still open is exactly the case this
exists to show you.

You clear each line the same way you clear any unconfirmed order: type what you saw into the box —
it says *"What you saw in ATAS — required"*, and the buttons stay switched off until you have — then
press the button that matches. Editing what you typed switches the buttons off again, because the
words are the assertion. Trading resumes when the last line is cleared.

**A second press while lines are still open is refused**, with the time of the first one: *"close-all
sent at 14:32; resolve it first."* There is no retry button, and there is no press that a failure can
leave stuck forever — the record is the press, and a person ends it.

**Close all positions re-reads your position immediately before it sends anything.** If it has moved
since you pressed, it stops and asks you again rather than closing a number that is no longer true.

**None of this survives in the app's memory.** Close TradeAgent in the middle of an emergency and
reopen it: the lines are still there, and trading is still paused. That is the point of writing them
down first.

### When an emergency cannot get an answer

An emergency press waits **two seconds** for ATAS, and no longer. Two seconds is not generous and is
not meant to be: someone pressing this button is trying to stop, and a button that sits there for
thirty seconds is a button that has failed them.

If the two seconds run out, you get this, and it is worth reading slowly:

> **'cancel-all' is NOT confirmed — check your positions and orders in ATAS.** The bridge is …; ….

(The word in quotes is whichever instruction it was: `cancel-all`, `close`, `cancel`, `place`.)

**What it is telling you.** TradeAgent asked ATAS to close everything and did not hear back in time.
It does **not** mean nothing was sent. It does not mean everything was sent. It means nobody knows
yet, and the only place the truth exists right now is ATAS itself.

**What it is asking you to do.** Open ATAS and look — at your positions, and at your working orders.
Whatever is on that screen is what is true. Then come back to the Dashboard and clear the lines with
what you saw. Do not press the emergency button again first; it will refuse, and it is right to.

If instead you see *"could not be read, so the operation was not started. Nothing was placed or
cancelled"*, that is the other kind: TradeAgent could not even read what it needed before starting,
and nothing went anywhere. Different sentence, different meaning, on purpose.

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

*Note on instruments:* an empty box allows **nothing**, not everything. Until you name at least one
instrument, every order is refused, and the box says so. That is on purpose: "I have not said which
ones yet" and "any of them" are different sentences, and only one of them is a decision you made.

## Two behaviours that will look like faults, but are not

**"AI trading paused — an earlier order is unconfirmed."**
TradeAgent sent an order and then lost contact before hearing back. The order might have reached your
broker, or might not. Rather than guess — or worse, send it again and risk two positions — it stops,
asks your broker what actually happened, and continues once it knows. This is the single most
important thing this software does. Let it finish.

**It also does this after a crash or a power cut.** When TradeAgent starts, any order that was still
being sent when it last stopped is marked as unknown, trading is paused, and the Activity page says
so: *"1 order(s) were still being sent when TradeAgent last stopped. Trading is paused until you or
the platform confirm what happened to them."* That pause happens before anything else can trade, and
it is not skippable.

**An order refused for breaking a limit, or because prices went stale.**
Working as intended. TradeAgent will not size an order from an out-of-date price, and it will not
exceed a limit you set. The reason appears in Activity.

## What works, and what is not finished

Told plainly, because you are the one who would find out the hard way.

### Trading through ATAS works

It was walked, once, on a real Windows machine on 31 August, on a simulated account inside ATAS, with
no black window at any point:

1. The AI asked to buy. Nothing was sent — the mode was **Real, ask me first**, so TradeAgent held
   the order and told the AI *"The AI is asking permission to place an order."*
2. The window raised its amber bar — *"The AI is asking permission — 1 order waiting"*, with
   **Review the request** — and the Dashboard showed the order: buy 1, the instrument, the price,
   the time it was asked, and **Approve** and **Decline**.
3. **Approve** was pressed twice ("Confirm: place this order").
4. It reached ATAS and came back with ATAS's own order number. ATAS's own Trading Activity panel
   showed the position, independently of TradeAgent's record.
5. It was cancelled, and the book was checked afterwards from outside TradeAgent: no orders, no
   position.

Since then, the piece inside ATAS refuses to place an order at all unless it has first written down
the order's own reference where a crash cannot lose it. So "the order was sent but nobody wrote it
down" is not a state this can end up in.

### Still not finished

- **Nothing has ever been tried with real money**, by anyone, on purpose. Everything above was on a
  simulated account.
- **Fully automatic trading is not available on ATAS**, and the Checks page says so in those words.
  It needs two things proven — that your order reference survives the round trip, and that order
  history reaches far enough back to answer "what happened to this one". Until a platform confirms
  both, **Real, fully automatic** is withheld and the other three modes work normally.
- **The blue "Windows protected your PC" screen has not been walked.** Nobody has yet installed this
  from a browser download on a machine that treats it as downloaded.
- **Rolling an update back has never been tried**, nor has an update interrupted halfway.
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

1. Press **STOP AI TRADING** if you are unsure. It is one press, it is instant, and it is safe. Do
   this first; everything else can wait.
2. Go to **Checks** and press **Check everything**. It tests each part in turn and names any problem
   in plain words. These checks never change anything.
3. Read **Activity** — it is a plain-language history of what happened, including every refusal and
   its reason.
4. Press **Create support package** on the Checks page and send the file to whoever helps you. It
   contains logs only — no passwords, no keys — and **Show the file** opens the folder it went to.

If it is your *positions* you are unsure about rather than the software, ATAS is the answer, not
this window. Whatever ATAS shows is what is true.

## Your records, and uninstalling

Everything TradeAgent keeps — your trading history, your settings, the AI's own work and notes — lives
in a folder in your own Windows account, not inside the program. Uninstalling TradeAgent leaves that
folder exactly where it is.

That is deliberate. An audit trail of what was traded and why is not something an uninstaller gets to
delete on your behalf. If you want it gone, delete it yourself.
