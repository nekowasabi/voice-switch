# v1: early wake probe (earlyWakeMs 300)

Train failures: wake-comma waits for the 2.5 s head cap (21/24 late >1 s); wake-lone and wake-pause land at 337-408 ms because the hangover (300 ms) plus STT runs after the word ends.
Change: turn on the prototype early probe, which runs STT on the open utterance at a 60 ms internal gap and every 300 ms. A wake-word prefix fires before the hangover.
Expected: comma cases drop under 1 s; lone and pause cases fire about 60 ms + STT after the word end. Risk: probe may fire on nonwake compounds earlier (already false-waking at hangover).
