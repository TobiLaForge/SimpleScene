// Copyright(C) David W. Jeske, 2014, All Rights Reserved.
#version 120

uniform mat4 viewMatrix;

#ifdef INSTANCE_DRAW
attribute vec3 cylinderCenter;
attribute vec3 cylinderAxis; // must be normalized
attribute float cylinderWidth;
attribute float cylinderLength;
attribute vec4 cylinderColor;
#else
uniform vec3 cylinderCenter;
uniform vec3 cylinderAxis; // must be normalized
uniform float cylinderWidth;
uniform float cylinderLength;
#endif
varying vec3 varCylCenter;
varying vec3 varCylXAxis;
varying vec3 varCylYAxis;
varying vec3 varCylZAxis;
varying float varCylLength;
varying float varCylWidth;
varying vec4 varCylColor;

void main()
{
#ifdef INSTANCE_DRAW
    vec4 color = cylinderColor * gl_Color;
#else
    vec4 color = gl_Color;
#endif
        
    if (abs(cylinderAxis.x) < 0.001 && abs(cylinderAxis.y) < 0.001) {
        varCylXAxis = vec3(1, 0, 0);
    } else {
        varCylXAxis = normalize(vec3(cylinderAxis.y, -cylinderAxis.x, 0));
    }
    varCylYAxis = normalize(cross(cylinderAxis, varCylXAxis));
    varCylZAxis = cylinderAxis;
    
    vec3 combinedWorldPos = cylinderCenter
        + gl_Vertex.x * cylinderAxis * cylinderLength  
        + gl_Vertex.y * varCylXAxis * cylinderWidth * 2
        + gl_Vertex.z * varCylYAxis * cylinderWidth * 2;
        
    gl_Position = gl_ProjectionMatrix * viewMatrix * vec4(combinedWorldPos, 1);
       
    varCylCenter = cylinderCenter;
    varCylWidth = cylinderWidth;
    varCylLength = cylinderLength;
    varCylColor = color;  
}
