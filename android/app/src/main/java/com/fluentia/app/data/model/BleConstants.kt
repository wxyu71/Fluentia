package com.fluentia.app.data.model

import java.util.UUID

object BleConstants {
    val SERVICE_UUID: UUID = UUID.fromString("21e2f7d4-4dc0-4b0d-a145-5f9b6459be10")
    val NOTIFY_CHARACTERISTIC_UUID: UUID = UUID.fromString("21e2f7d4-4dc0-4b0d-a145-5f9b6459be11")
    val WRITE_CHARACTERISTIC_UUID: UUID = UUID.fromString("21e2f7d4-4dc0-4b0d-a145-5f9b6459be12")
}
