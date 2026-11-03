"""Finder layout for the distributable image (read by dmgbuild)."""
import os

files = [os.environ["DISCORD_DMG_APP"]]
symlinks = {"Applications": "/Applications"}
background = os.environ["DISCORD_DMG_BACKGROUND"]
format = "UDZO"
filesystem = "HFS+"
window_rect = ((200, 160), (640, 400))
show_status_bar = False
show_tab_view = False
show_toolbar = False
show_pathbar = False
show_sidebar = False
icon_size = 112
text_size = 14
arrange_by = None
icon_locations = {"Discord Timer.app": (165, 210), "Applications": (475, 210)}
