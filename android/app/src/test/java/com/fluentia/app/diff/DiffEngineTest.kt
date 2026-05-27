package com.fluentia.app.diff

import org.junit.Assert.assertEquals
import org.junit.Test

class DiffEngineTest {

    @Test
    fun `identical text returns zero diff`() {
        val diff = DiffEngine.computeDiff("hello", "hello")
        assertEquals(0, diff.backspace)
        assertEquals("", diff.insert)
    }

    @Test
    fun `insert at end`() {
        val diff = DiffEngine.computeDiff("hello", "hello world")
        assertEquals(0, diff.backspace)
        assertEquals(" world", diff.insert)
    }

    @Test
    fun `delete from end`() {
        val diff = DiffEngine.computeDiff("hello world", "hello")
        assertEquals(6, diff.backspace) // " world" is 6 graphemes
        assertEquals("", diff.insert)
    }

    @Test
    fun `replace all`() {
        val diff = DiffEngine.computeDiff("abc", "xyz")
        assertEquals(3, diff.backspace)
        assertEquals("xyz", diff.insert)
    }

    @Test
    fun `insert in middle via prefix`() {
        // "abcdef" → "abcXYZdef"
        // Common prefix: "abc" (3 chars)
        // Suffix: "def" (3 graphemes) → backspace 3
        // Insert: "XYZdef"
        val diff = DiffEngine.computeDiff("abcdef", "abcXYZdef")
        assertEquals(3, diff.backspace)
        assertEquals("XYZdef", diff.insert)
    }

    @Test
    fun `empty to text`() {
        val diff = DiffEngine.computeDiff("", "hello")
        assertEquals(0, diff.backspace)
        assertEquals("hello", diff.insert)
    }

    @Test
    fun `text to empty`() {
        val diff = DiffEngine.computeDiff("hello", "")
        assertEquals(5, diff.backspace)
        assertEquals("", diff.insert)
    }

    @Test
    fun `grapheme counting - basic ASCII`() {
        assertEquals(5, DiffEngine.graphemeLength("hello"))
    }

    @Test
    fun `grapheme counting - emoji`() {
        // Single emoji: 😀 is 1 grapheme
        assertEquals(1, DiffEngine.graphemeLength("😀"))
    }

    @Test
    fun `grapheme counting - flag emoji`() {
        // 🇺🇸 is 1 grapheme (2 regional indicator symbols)
        assertEquals(1, DiffEngine.graphemeLength("🇺🇸"))
    }

    @Test
    fun `grapheme counting - ZWJ sequence`() {
        // 👨‍👩‍👧‍👦 is 1 grapheme (family emoji via ZWJ)
        assertEquals(1, DiffEngine.graphemeLength("👨‍👩‍👧‍👦"))
    }

    @Test
    fun `grapheme counting - skin tone modifier`() {
        // 👋🏽 is 1 grapheme (base + modifier)
        assertEquals(1, DiffEngine.graphemeLength("👋🏽"))
    }

    @Test
    fun `grapheme counting - mixed`() {
        // "a😀b" = 3 graphemes
        assertEquals(3, DiffEngine.graphemeLength("a😀b"))
    }

    @Test
    fun `grapheme counting - empty`() {
        assertEquals(0, DiffEngine.graphemeLength(""))
    }

    @Test
    fun `diff with emoji backspace count`() {
        // "hello😀" → "hello"
        // prefix: "hello" (5 chars), suffix: "😀" (1 grapheme)
        val diff = DiffEngine.computeDiff("hello😀", "hello")
        assertEquals(1, diff.backspace)
        assertEquals("", diff.insert)
    }

    @Test
    fun `CJK text diff`() {
        val diff = DiffEngine.computeDiff("你好世界", "你好")
        assertEquals(2, diff.backspace)
        assertEquals("", diff.insert)
    }
}
