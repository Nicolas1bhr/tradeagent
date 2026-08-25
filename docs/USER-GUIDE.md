# TradeAgent — what it is and how to use it

Written for the person using it, not for a programmer. Nothing here needs a command prompt.

## What it does

TradeAgent gives an AI assistant a safe, limited way to see your trading account in ATAS and to place
orders in it — with you holding every switch.

- **ATAS stays your trading screen.** TradeAgent does not replace it. Your charts, your broker login
  and your orders are still in ATAS.
- **The AI works in its own folder** on your computer. It can write notes, write its own tools, and
  look things up on the internet.
- **The AI cannot log in to your broker.** It has no access to your password. That is a deliberate
  part of the design, not an oversight.
- **It starts in practice mode.** Nothing real can happen until you switch real money on yourself.

## Setting it up

Run `TradeAgent-Setup-x64.exe` and follow the steps. It will:

1. explain what it is about to do;
2. check your computer has what it needs;
3. ask which AI assistant you want, and install it for you;
4. open a browser so you can sign in to your AI account;
5. find ATAS and install a small add-on into it;
6. ask you to start that add-on inside ATAS, once — it then notices by itself;
7. find your account, check prices are arriving, and check that orders are allowed;
8. create the AI's folder and start the AI.

If you close TradeAgent, or restart Windows, or something fails halfway — it picks up where it stopped.
It will not make you start again.

You should not need to type a command at any point. If you do, that is a fault worth reporting.

## The main screen

A list of green, orange and red dots showing what is working, and:

- **Start AI / Stop AI** — turns the assistant on and off.
- **Open the AI's folder** — where its notes and work live.
- **Open ATAS**.
- **Mode** — four settings, explained below.
- **Check everything** — runs every check and tells you in plain words what to do about any problem.
- **Create support package** — a single file you can send to whoever helps you. It contains logs, and
  no passwords.

And three separate emergency buttons. They are separate on purpose:

| Button | What it does | What it does *not* do |
|---|---|---|
| **STOP AI TRADING** | Takes away the AI's permission to trade, instantly | Does not touch your existing orders or positions |
| **Cancel all working orders** | Removes orders that have not filled yet | Does not close positions you already hold |
| **Close all positions** | Sells/buys to flatten everything, at market | — |

Each needs two presses. No single click can empty your account by accident.

## The four modes

| Mode | Meaning |
|---|---|
| **Watch only** | The AI can look at everything and place nothing. |
| **Practice** | Orders go to a simulated account. Nothing real. Start here and stay a while. |
| **Real, ask me first** | The AI proposes each order; nothing happens until you approve it. |
| **Real, fully automatic** | The AI trades on its own, inside the limits you set. |

The two real-money modes also need you to switch real-money trading on separately. If you leave a real
mode and come back, you have to switch it on again — it does not remember your permission.

## The safety limits

Set in the app, enforced before anything reaches your broker. The AI cannot raise them and has no
command to ask:

- most it can buy or sell in one order;
- most money one order may be worth (off by default — see the note below);
- how many positions it may hold at once;
- how many orders per minute;
- which instruments it may touch at all.

The defaults are deliberately small. Start there.

*Note on order value:* for futures this limit is off by default, because a single contract is worth a
very large amount on paper while needing much less to trade. A value limit set carelessly would block
every ordinary order. The number of contracts is the limit that means something for futures.

## Two behaviours that will look like faults, but are not

**"AI trading paused — an earlier order is unconfirmed."**
TradeAgent sent an order and then lost contact before hearing back. The order might have reached your
broker, or might not. Rather than guess — or worse, send it again and risk two positions — it stops,
asks your broker what actually happened, and continues once it knows. This is the single most important
thing this software does. Let it finish.

**An order refused for breaking a limit, or because prices went stale.**
Working as intended. TradeAgent will not size an order from an out-of-date price, and it will not
exceed a limit you set. The reason appears in Recent activity.

## Honestly, about making money

The person who built this for you would rather say it plainly: **do not expect this to be profitable.**
You are competing with firms whose computers sit inside the exchange and who pay for news feeds that
arrive before yours. An AI assistant on a laptop does not close that gap.

What this software is actually good at is being *safe and honest*: it will not place an order twice,
it will not trade when it cannot tell what your account holds, it stops instantly when you tell it to,
and it writes down everything it did. Treat it as something interesting to run in practice mode and
learn from. If you ever go to real money, go with the smallest size your broker allows, and read the
Recent activity list.

## If something goes wrong

1. Press **Check everything**. It usually names the problem and what to do.
2. Read **Recent activity** — it is a plain-language history of what happened, most recent last.
3. Press **STOP AI TRADING** if you are unsure. It is instant, it is safe, and it changes nothing about
   your money.
4. Press **Create support package** and send the file to whoever helps you with this.

Your trading records and the AI's work stay in a folder on your computer even if you uninstall
TradeAgent. Nothing is deleted behind your back.
