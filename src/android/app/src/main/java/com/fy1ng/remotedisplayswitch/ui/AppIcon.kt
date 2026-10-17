package com.fy1ng.remotedisplayswitch.ui

import androidx.compose.foundation.Canvas
import androidx.compose.foundation.layout.size
import androidx.compose.material3.LocalContentColor
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.CornerRadius
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.graphics.drawscope.withTransform
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.unit.dp

internal enum class AppIconType { Monitor, Power, Settings, Back, Down, Add, Check, Refresh }

@Composable
internal fun AppIcon(type: AppIconType, modifier: Modifier = Modifier, description: String? = null,
    tint: Color = LocalContentColor.current) {
    Canvas(modifier.size(24.dp).then(if (description == null) Modifier else Modifier.semantics {
        contentDescription = description
    })) {
        withTransform({ scale(size.width / 24f, size.height / 24f, Offset.Zero) }) {
            val stroke = Stroke(width = 1.8f, cap = StrokeCap.Round)
            fun line(x1: Float, y1: Float, x2: Float, y2: Float) =
                drawLine(tint, Offset(x1, y1), Offset(x2, y2), strokeWidth = 1.8f, cap = StrokeCap.Round)
            when (type) {
                AppIconType.Monitor -> {
                    drawRoundRect(tint, Offset(3f, 4f), Size(18f, 13f), CornerRadius(2f), style = stroke)
                    line(12f, 17f, 12f, 21f); line(8f, 21f, 16f, 21f)
                }
                AppIconType.Power -> {
                    drawArc(tint, -50f, 280f, false, Offset(5f, 5f), Size(14f, 14f), style = stroke)
                    line(12f, 3f, 12f, 12f)
                }
                AppIconType.Settings -> {
                    line(6f, 4f, 6f, 20f); line(12f, 4f, 12f, 20f); line(18f, 4f, 18f, 20f)
                    line(3f, 8f, 9f, 8f); line(9f, 16f, 15f, 16f); line(15f, 10f, 21f, 10f)
                }
                AppIconType.Back -> {
                    line(19f, 12f, 5f, 12f); line(5f, 12f, 11f, 6f); line(5f, 12f, 11f, 18f)
                }
                AppIconType.Down -> { line(6f, 9f, 12f, 15f); line(12f, 15f, 18f, 9f) }
                AppIconType.Add -> { line(5f, 12f, 19f, 12f); line(12f, 5f, 12f, 19f) }
                AppIconType.Check -> { line(4f, 12f, 10f, 18f); line(10f, 18f, 20f, 6f) }
                AppIconType.Refresh -> {
                    drawArc(tint, 30f, 290f, false, Offset(4f, 4f), Size(16f, 16f), style = stroke)
                    line(18f, 3f, 20f, 8f); line(20f, 8f, 15f, 8f)
                }
            }
        }
    }
}
