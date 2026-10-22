package com.fy1ng.fy1switch.ui

import androidx.annotation.DrawableRes
import androidx.compose.foundation.layout.size
import androidx.compose.material3.Icon
import androidx.compose.material3.LocalContentColor
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.unit.dp
import com.fy1ng.fy1switch.R

internal enum class AppIconType(@get:DrawableRes val resource: Int) {
    Monitor(R.drawable.ic_fluent_desktop_24_regular),
    MonitorOff(R.drawable.ic_fluent_desktop_off_24_regular),
    Power(R.drawable.ic_fluent_power_24_regular),
    Settings(R.drawable.ic_fluent_settings_24_regular),
    Back(R.drawable.ic_fluent_arrow_left_24_regular),
    Down(R.drawable.ic_fluent_chevron_down_24_regular),
    Up(R.drawable.ic_fluent_chevron_up_24_regular),
    Add(R.drawable.ic_fluent_add_24_regular),
    Check(R.drawable.ic_fluent_checkmark_24_regular),
    Refresh(R.drawable.ic_fluent_arrow_sync_24_regular),
    Save(R.drawable.ic_fluent_save_24_regular),
    Phone(R.drawable.ic_fluent_phone_24_regular),
    Scan(R.drawable.ic_fluent_qr_code_24_regular),
    Paste(R.drawable.ic_fluent_clipboard_paste_24_regular),
    Delete(R.drawable.ic_fluent_delete_24_regular),
    Question(R.drawable.ic_fluent_question_circle_24_regular),
}

@Composable
internal fun AppIcon(
    type: AppIconType,
    modifier: Modifier = Modifier,
    description: String? = null,
    tint: Color = LocalContentColor.current,
) {
    Icon(
        painter = painterResource(type.resource),
        contentDescription = description,
        modifier = modifier.size(24.dp),
        tint = tint,
    )
}
