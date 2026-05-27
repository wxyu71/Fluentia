package com.fluentia.app.diff

import com.fluentia.app.data.model.TextDiff
import java.text.BreakIterator
import java.util.Locale

object DiffEngine {

    fun computeDiff(oldText: String, newText: String): TextDiff {
        if (oldText == newText) return TextDiff(backspace = 0, insert = "")

        val minLen = minOf(oldText.length, newText.length)
        var prefix = 0
        while (prefix < minLen && oldText[prefix] == newText[prefix]) {
            prefix++
        }

        val suffix = oldText.substring(prefix)
        return TextDiff(
            backspace = graphemeLength(suffix),
            insert = newText.substring(prefix),
        )
    }

    fun graphemeLength(text: String): Int {
        if (text.isEmpty()) return 0
        val iterator = BreakIterator.getCharacterInstance(Locale.ROOT)
        iterator.setText(text)
        var count = 0
        while (iterator.next() != BreakIterator.DONE) count++
        return count
    }
}
