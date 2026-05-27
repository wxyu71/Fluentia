package com.fluentia.app.di

import com.fluentia.app.crypto.AndroidSodiumProvider
import com.fluentia.app.crypto.SodiumProvider
import com.fluentia.app.data.constants.AppConstants
import dagger.Module
import dagger.Provides
import dagger.hilt.InstallIn
import dagger.hilt.components.SingletonComponent
import kotlinx.serialization.json.Json
import okhttp3.OkHttpClient
import java.util.concurrent.TimeUnit
import javax.inject.Singleton

@Module
@InstallIn(SingletonComponent::class)
object AppModule {

    @Provides
    @Singleton
    fun provideSodiumProvider(): SodiumProvider = AndroidSodiumProvider()

    @Provides
    @Singleton
    fun provideOkHttpClient(): OkHttpClient = OkHttpClient.Builder()
        .connectTimeout(AppConstants.CONNECT_TIMEOUT_MS, TimeUnit.MILLISECONDS)
        .readTimeout(0, TimeUnit.MILLISECONDS)
        .writeTimeout(10, TimeUnit.SECONDS)
        .pingInterval(AppConstants.HEARTBEAT_INTERVAL_MS, TimeUnit.MILLISECONDS)
        .build()

    @Provides
    @Singleton
    fun provideJson(): Json = Json {
        encodeDefaults = false
        ignoreUnknownKeys = true
    }
}
