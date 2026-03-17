# Copilot Instructions

## Project Guidelines
- In this project, FullLeftPosition and FullRightPosition in AxisSettings are absolute encoder values (not relative offsets from CenterPosition). The axis range is 0-1024 with 512 as center (ProSim). Calibration stores raw encoder positions; the offset is calculated on restart by centering and comparing current encoder to stored CenterPosition.