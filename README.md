# SolidWorks to URDF Exporter

SW2025 自定义版当前为 **1.7.11**，包含独立配置窗口、自动草稿、连续拾取、URDF 结构修复、坐标系预检和覆盖质量属性读取。

- [使用说明](docs/使用说明_v1.7.11.md)
- [版本问题与修复记录](VERSION_HISTORY.md)
- [构建及各版技术说明](INDEPENDENT-WINDOW.md)

1.7.11 已完成构建和 99 项隔离检查，真实 SolidWorks 中的整组覆盖值读取及完整导出尚需实机确认。源码基于 [ros/solidworks_urdf_exporter](https://github.com/ros/solidworks_urdf_exporter)，保留原项目 MIT 许可。以下为上游项目说明。

Authored and maintained by [Stephen Brawner](brawner@gmail.com). Past supporters include [PickNik Consulting](https://picknik.ai), Verb Surgical, Open Robotics, and Willow Garage. 

## Latest Release
https://github.com/ros/solidworks_urdf_exporter/releases/tag/1.6.0

## SolidWorks Version Requirements

1. The minimum required version of SolidWorks for use with this add-in is 2018 Service Pack 5. SolidWorks 2017 or earlier may work. See [this issue](https://github.com/ros/solidworks_urdf_exporter/issues/73).

## Usage

See the [ROS Wiki](http://wiki.ros.org/sw_urdf_exporter) and associated [tutorials](http://wiki.ros.org/sw_urdf_exporter/Tutorials).

## Development

1. Install Visual Studio 2017
1. Install .NET desktop development
    1. From Visual Studio: `Tools > Get Tools and Features...`
    1. Check `.NET desktop development` package
    1. Select `Modify`
1. Launch Visual Studio with admin privileges. Right click and select `Run as Administrator`
1. Open `sw2urdf/SW2URDF.sln`  
1. Enable Debugging
    1. Right click `SW2URDF` in the Solution Explorer
    1. Click the `Debug` Tab
    1. Ensure `Configuration:` is set to `Debug`
    1. Ensure `Start external program:` is pointing to the SolidWorks executable. For example `C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS\SLDWORKS.exe`

### Trouble Shooting

1. `AxImp.exe` error - Check the installation of the .Net Tools. If there is no error, install the Windows 10 SDK.
1. `Resourse.resx` error - Check if `sw2urdf/SW2URDF/Resources.resx` exists and is empty. If empty, delete this file then right click the `SW2URDF` in the Solution Explorer and select `Properties`. Navigate to the Resources tab and click the button to create a new file.
