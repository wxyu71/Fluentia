/**
 * Application-wide constants.
 * Extracted here for easy tuning without digging into component code.
 */

/** Debounce window (ms) for diff commands sent from mobile to desktop.
 *  Rapid keystrokes within this window are coalesced into a single diff. */
export const DIFF_DEBOUNCE_MS = 60;

/** Batch window (ms) on the desktop side for merging incoming diffs.
 *  Must be >= DIFF_DEBOUNCE_MS to ensure the desktop captures the debounced diff
 *  plus any late-arriving follow-ups in a single atomic SendInput call. */
export const DIFF_BATCH_WINDOW_MS = 100;
