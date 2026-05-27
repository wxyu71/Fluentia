# kotlinx.serialization
-keepattributes *Annotation*, InnerClasses
-dontnote kotlinx.serialization.AnnotationsKt
-keepclassmembers class kotlinx.serialization.json.** { *** Companion; }
-keepclasseswithmembers class kotlinx.serialization.json.** { kotlinx.serialization.KSerializer serializer(...); }
-keep,includedescriptorclasses class com.fluentia.app.**$$serializer { *; }
-keepclassmembers class com.fluentia.app.** { *** Companion; }
-keepclasseswithmembers class com.fluentia.app.** { kotlinx.serialization.KSerializer serializer(...); }

# LibSodium / JNA
-keep class com.sun.jna.** { *; }
-keep class org.libsodium.** { *; }
-keep class com.goterl.lazysodium.** { *; }
