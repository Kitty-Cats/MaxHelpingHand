﻿module MaxHelpingHandSnowCustomColors

using ..Ahorn, Maple

@mapdef Effect "MaxHelpingHand/SnowCustomColors" SnowCustomColors(only::String="*", exclude::String="", colors::String="FF0000,00FF00,0000FF")

placements = SnowCustomColors

function Ahorn.canFgBg(effect::SnowCustomColors)
    return true, true
end

end
