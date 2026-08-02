# Hardware session checklists (user-executed, tool-driven)

Preconditions for EVERY hardware session:
- [ ] Radio battery well charged (interrupted flash writes are the main corruption vector)
- [ ] Programming cable fully seated at both ends; radio powered ON
- [ ] No other program has the serial port open (CPS, modem-manager). Debian:
      `sudo systemctl stop ModemManager` if it grabs /dev/ttyUSB0
- [ ] Serial permission: user in `dialout` (`sudo usermod -aG dialout $USER` + re-login)

## A. Bring-up ladder (once, during P2) — implementation spec §3.5

| Step | Command | Gate to next step |
|---|---|---|
| 1. Identify | `plugmatic dev identify` | Model = DP570UV; identify response archived in the dev run |
| 2. Full read ×3 | `plugmatic dev dump` ×3 | The three images byte-compare clean (differences → volatile regions recorded in dm32uv-format.md §14). First run's manifest tagged `factory-golden` |
| 3. Decode | `plugmatic dev decode <run>/dump.bin` | IR summary matches radio's on-screen state (spot checks: channel names, freqs, zones) |
| 4. No-op write | `plugmatic dev writeback <read-run-dir>` | Radio behaves identically after reboot; post-write compare clean |
| 5. Single-channel plug | `plugmatic write --plug <single-channel.yaml>` | Channel visible & correct on radio screen; read-back compare clean |
| 6. Full generated plug | `plugmatic write --plug <generated>` | Spot checks on radio |

## B. Release validation (per release)

- [ ] `plugmatic doctor` clean on Debian 13 and Windows 11
- [ ] `plugmatic read` ×3 byte-stable modulo documented masks
- [ ] `plugmatic dev writeback` no-op clean
- [ ] Full Larimer County plug generated, written, spot-checked on-radio
- [ ] Restore drill: `plugmatic write --image <run>/pre-write.bin` returns radio to prior state
